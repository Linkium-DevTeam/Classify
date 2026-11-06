using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace Classify.Services;

/// <summary>
/// DoH（DNS over HTTPS）解析层，防 DNS 污染/劫持：
/// 提供商回退链 AliDNS → DNSPod → Cloudflare → 系统 DNS 兜底；
/// 结果按 TTL 缓存（60s~30min），失败负缓存 30s；连续失败自动熔断 5 分钟，避免每次连接都卡 9 秒。
///
/// 关键实现：URL 仍用提供商域名（保证 TLS SNI/证书校验正确），
/// 通过 SocketsHttpHandler.ConnectCallback 把连接指向系统 DNS/内置 IP。
/// 旧实现直接请求 https://IP 并改 Host 头——SNI 仍是 IP，证书校验必然失败。
/// </summary>
public static class DohResolver
{
    private static readonly (string Host, string PathTemplate)[] Providers =
    {
        ("dns.alidns.com", "/resolve?name={0}&type=A"),
        ("doh.pub", "/dns-query?name={0}&type=A"),
        ("cloudflare-dns.com", "/dns-query?name={0}&type=A"),
    };

    private static readonly ConcurrentDictionary<string, (IPAddress[] Ips, DateTime Expires)> Cache = new();
    private static readonly ConcurrentDictionary<string, DateTime> NegativeUntil = new();
    private static readonly ConcurrentDictionary<string, (string Ip, DateTime Expires)> ProviderIp = new();

    /// <summary>全部提供商失败后的熔断截止时间（UTC）。</summary>
    private static DateTime _disabledUntil = DateTime.MinValue;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectCallback = async (ctx, ct) =>
        {
            var ip = await ResolveProviderIpAsync(ctx.DnsEndPoint.Host);
            if (ip is null || !IPAddress.TryParse(ip, out var addr))
                throw new HttpRequestException("DoH 提供商地址不可用");
            var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(addr, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    })
    { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>
    /// 解析 host 的 A 记录。返回 null 表示 DoH 全部失败/已熔断（调用方应回退系统 DNS 直连）。
    /// </summary>
    public static async Task<IPAddress[]?> ResolveAsync(string host)
    {
        // 稳定性二分开关：设置环境变量 CLASSIFY_NO_DOH=1 可禁用 DoH
        try { if (Environment.GetEnvironmentVariable("CLASSIFY_NO_DOH") == "1") return null; }
        catch { }
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (IPAddress.TryParse(host, out _)) return null; // 已是 IP
        if (Uri.CheckHostName(host) != UriHostNameType.Dns) return null;

        if (Cache.TryGetValue(host, out var hit) && hit.Expires > DateTime.UtcNow) return hit.Ips;
        if (NegativeUntil.TryGetValue(host, out var neg) && neg > DateTime.UtcNow) return null;
        if (_disabledUntil > DateTime.UtcNow) return null;

        foreach (var (providerHost, pathTemplate) in Providers)
        {
            try
            {
                var url = $"https://{providerHost}" + string.Format(pathTemplate, Uri.EscapeDataString(host));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("accept", "application/dns-json");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
                if (!doc.RootElement.TryGetProperty("Status", out var st) || !st.TryGetInt32(out var status) || status != 0)
                    continue;
                if (!doc.RootElement.TryGetProperty("Answer", out var answers)) continue;

                var ips = new List<IPAddress>();
                uint minTtl = 3600;
                foreach (var a in answers.EnumerateArray())
                {
                    if (!a.TryGetProperty("type", out var t) || !t.TryGetInt32(out var type) || type != 1) continue;
                    if (!a.TryGetProperty("data", out var d) || !IPAddress.TryParse(d.GetString(), out var addr)) continue;
                    ips.Add(addr);
                    if (a.TryGetProperty("TTL", out var ttl) && ttl.TryGetInt32(out var ttlSec))
                        minTtl = Math.Min(minTtl, (uint)Math.Max(0, ttlSec));
                }
                if (ips.Count == 0) continue;

                var expires = DateTime.UtcNow.AddSeconds(Math.Clamp(minTtl, 60, 1800));
                Cache[host] = (ips.ToArray(), expires);
                NegativeUntil.TryRemove(host, out _);
                return ips.ToArray();
            }
            catch
            {
                // 该提供商网络/TLS 异常：下次重新解析提供商 IP，继续下一个
                ProviderIp.TryRemove(providerHost, out _);
            }
        }

        NegativeUntil[host] = DateTime.UtcNow.AddSeconds(30);
        _disabledUntil = DateTime.UtcNow.AddMinutes(5);
        return null;
    }

    /// <summary>提供商主机 → IP：优先系统 DNS（一次，2 秒上限），失败用内置已知 IP 兜底。</summary>
    private static async Task<string?> ResolveProviderIpAsync(string providerHost)
    {
        if (ProviderIp.TryGetValue(providerHost, out var cached) && cached.Expires > DateTime.UtcNow)
            return cached.Ip;

        string? ip = null;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(providerHost).WaitAsync(TimeSpan.FromSeconds(2));
            ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch { }
        if (ip is null)
        {
            // 内置引导 IP（提供商官方 anycast，证书含对应 SAN）
            ip = providerHost switch
            {
                "dns.alidns.com" => "223.5.5.5",
                "doh.pub" => "1.12.12.12",
                "cloudflare-dns.com" => "1.1.1.1",
                _ => null,
            };
        }
        if (ip != null) ProviderIp[providerHost] = (ip, DateTime.UtcNow.AddMinutes(30));
        return ip;
    }
}
