using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace Classify.Services;

/// <summary>
/// DoH（DNS over HTTPS）解析层，防 DNS 污染/劫持：
/// 提供商回退链 AliDNS → DNSPod → Cloudflare → 系统 DNS 兜底；
/// 结果按 TTL 缓存（60s~30min），失败负缓存 30s，全程不阻塞（3s 超时）。
/// 仅用于业务域名：DoH 提供商自身通过系统 DNS 引导解析一次并长期缓存。
/// </summary>
public static class DohResolver
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // 提供商主机走系统 DNS 引导（一次性），之后用缓存的连接
        PooledConnectionLifetime = TimeSpan.FromHours(12),
    })
    { Timeout = TimeSpan.FromSeconds(3) };

    private static readonly (string Host, string PathTemplate)[] Providers =
    {
        ("dns.alidns.com", "/resolve?name={0}&type=A"),
        ("doh.pub", "/dns-query?name={0}&type=A"),
        ("cloudflare-dns.com", "/dns-query?name={0}&type=A"),
    };

    private static readonly ConcurrentDictionary<string, (IPAddress[] Ips, DateTime Expires)> Cache = new();
    private static readonly ConcurrentDictionary<string, DateTime> NegativeUntil = new();
    private static readonly ConcurrentDictionary<string, string> ProviderIp = new();

    /// <summary>
    /// 解析 host 的 A 记录。返回 null 表示 DoH 全部失败（调用方应回退系统 DNS 直连）。
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

        foreach (var (providerHost, pathTemplate) in Providers)
        {
            try
            {
                var ip = await ResolveProviderIpAsync(providerHost);
                if (ip is null) continue;

                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://{ip}{string.Format(pathTemplate, Uri.EscapeDataString(host))}");
                req.Headers.Host = providerHost; // SNI/Host 与证书域一致
                req.Headers.TryAddWithoutValidation("accept", "application/dns-json");
                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
                if (!doc.RootElement.TryGetProperty("Status", out var st) || st.GetInt32() != 0) continue;
                if (!doc.RootElement.TryGetProperty("Answer", out var answers)) continue;

                var ips = new List<IPAddress>();
                uint minTtl = 3600;
                foreach (var a in answers.EnumerateArray())
                {
                    if (a.TryGetProperty("type", out var t) && t.GetInt32() == 1 &&
                        a.TryGetProperty("data", out var d) &&
                        IPAddress.TryParse(d.GetString(), out var addr))
                    {
                        ips.Add(addr);
                        if (a.TryGetProperty("TTL", out var ttl)) minTtl = Math.Min(minTtl, (uint)ttl.GetInt32());
                    }
                }
                if (ips.Count == 0) continue;

                var expires = DateTime.UtcNow.AddSeconds(Math.Clamp(minTtl, 60, 1800));
                Cache[host] = (ips.ToArray(), expires);
                NegativeUntil.TryRemove(host, out _);
                return ips.ToArray();
            }
            catch { /* 换下一个提供商 */ }
        }

        NegativeUntil[host] = DateTime.UtcNow.AddSeconds(30);
        return null;
    }

    /// <summary>提供商主机 → IP：优先系统 DNS（一次），失败用内置已知 IP 兜底。</summary>
    private static async Task<string?> ResolveProviderIpAsync(string providerHost)
    {
        if (ProviderIp.TryGetValue(providerHost, out var cached)) return cached;
        string? ip = null;
        try
        {
            // 系统 DNS 不支持取消，挂 2 秒上限防卡死（校园网/故障 DNS 场景），超时走内置引导 IP
            var addrs = await Dns.GetHostAddressesAsync(providerHost).WaitAsync(TimeSpan.FromSeconds(2));
            ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?.ToString();
        }
        catch { }
        // 内置引导 IP（提供商官方 anycast，证书含对应 SAN）
        if (ip is null)
        {
            ip = providerHost switch
            {
                "dns.alidns.com" => "223.5.5.5",
                "doh.pub" => "1.12.12.12",
                "cloudflare-dns.com" => "1.1.1.1",
                _ => null,
            };
        }
        if (ip != null) ProviderIp[providerHost] = ip;
        return ip;
    }
}
