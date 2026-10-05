using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Classify.Models;

namespace Classify.Services;

/// <summary>Cloudflare Worker 云端 API 客户端（设备侧）。DoH 解析 + 备用域名切换。</summary>
public sealed partial class ApiService
{
    private static readonly HttpClient _http = new(SharedHandler())
    { Timeout = TimeSpan.FromSeconds(40) };

    static ApiService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Classify/3.0");
    }

    /// <summary>共享连接处理器：ConnectCallback 按 DoH 解析结果直连 IP（证书仍按域名校验），DoH 失败回退系统 DNS。</summary>
    private static SocketsHttpHandler SharedHandler()
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var disableCb = false;
        try { disableCb = Environment.GetEnvironmentVariable("CLASSIFY_NO_CONNECTCB") == "1"; } catch { }
        if (!disableCb)
            h.ConnectCallback = async (ctx, ct) =>
            {
                var host = ctx.DnsEndPoint.Host;
                var ips = await DohResolver.ResolveAsync(host);
                if (ips is not null)
                {
                    foreach (var ip in ips)
                    {
                        try
                        {
                            var socket = new Socket(ip.AddressFamily,
                                SocketType.Stream, ProtocolType.Tcp);
                            await socket.ConnectAsync(ip, ctx.DnsEndPoint.Port, ct);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch { /* 试下一个 IP */ }
                    }
                }
                // 回退：系统 DNS 正常连接
                var fallback = new Socket(AddressFamily.InterNetwork,
                    SocketType.Stream, ProtocolType.Tcp);
                await fallback.ConnectAsync(ctx.DnsEndPoint, ct);
                return new NetworkStream(fallback, ownsSocket: true);
            };
        return h;
    }

    /// <summary>当前请求域名（可随网络故障自动切换）。</summary>
    private static string Base => ServerEndpoints.Current;

    /// <summary>发送并跟踪网络级失败（触发备用域名切换）。HTTP 状态错误不算网络失败。</summary>
    private async Task<HttpResponseMessage> SendTrackedAsync(HttpRequestMessage req,
        HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            var resp = await _http.SendAsync(req, option);
            ServerEndpoints.ReportSuccess(Base);
            return resp;
        }
        catch (HttpRequestException)
        {
            ServerEndpoints.ReportFailure(Base);
            throw;
        }
        catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        {
            ServerEndpoints.ReportFailure(Base);
            throw new HttpRequestException("请求超时", ex);
        }
    }

    private HttpRequestMessage Req(HttpMethod m, string path)
    {
        var req = new HttpRequestMessage(m, Base + path);
        var token = SettingsService.Instance.Values.DeviceToken;
        if (!string.IsNullOrEmpty(token)) req.Headers.Add("authorization", "Bearer " + token);
        return req;
    }

    /// <summary>解析服务端 messages 载荷（HTTP 长轮询与 WebSocket 共用）。</summary>
    public static (List<InboxMessage> List, long MaxSeq) ParseMessages(JsonElement data)
    {
        var list = new List<InboxMessage>();
        long maxSeq = 0;
        if (data.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                var msg = new InboxMessage
                {
                    Seq = m.TryGetProperty("seq", out var sq) ? sq.GetInt64() : 0,
                    Type = m.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "",
                    Mid = m.TryGetProperty("payload", out var pl0) && pl0.TryGetProperty("mid", out var md) ? md.GetString() ?? "" : "",
                    RawPayload = m.TryGetProperty("payload", out var plR) ? plR.GetRawText() : "{}",
                    From = (m.TryGetProperty("from", out var fm) ? fm.GetString() : null)
                         ?? (m.TryGetProperty("payload", out var plFm) && plFm.TryGetProperty("from", out var fm2) ? fm2.GetString() ?? "" : ""),
                    Created = m.TryGetProperty("created", out var cr) ? cr.GetInt64() : 0,
                };
                if (m.TryGetProperty("payload", out var pl))
                {
                    if (pl.TryGetProperty("text", out var tx)) msg.Text = tx.GetString() ?? "";
                    if (pl.TryGetProperty("speak", out var sp)) msg.Speak = sp.GetBoolean();
                    if (pl.TryGetProperty("fid", out var fd)) msg.FileId = fd.GetString() ?? "";
                    if (pl.TryGetProperty("name", out var nm)) msg.FileName = nm.GetString() ?? "";
                    if (pl.TryGetProperty("size", out var sz)) msg.FileSize = sz.GetInt64();
                    if (msg.Type == "cmd" && pl.TryGetProperty("name", out var cn)) msg.CmdName = cn.GetString() ?? "";
                    if (msg.Type == "cmd" && pl.TryGetProperty("value", out var cv))
                        msg.CmdValue = cv.ValueKind == JsonValueKind.Null ? null : cv.ToString();
                    // surface 通道：action/minutes（clock 等）
                    if (pl.TryGetProperty("action", out var ac)) msg.CmdName = ac.GetString() ?? "";
                    if (pl.TryGetProperty("minutes", out var mn)) msg.CmdValue = mn.GetRawText();
                }
                list.Add(msg);
                if (msg.Seq > maxSeq) maxSeq = msg.Seq;
            }
        }
        return (list, maxSeq);
    }

    private static async Task<JsonElement> SendAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            string msg = $"服务器返回 {(int)resp.StatusCode}";
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("message", out var m)) msg = m.GetString() ?? msg;
            }
            catch { }
            throw new HttpRequestException(msg);
        }
        using var doc2 = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return doc2.RootElement.Clone();
    }

    /// <summary>服务端要求的最低客户端版本（/api/version 下发；低于此版本时加快更新检查节奏）。</summary>
    public static string? ServerMinVersion { get; private set; }

    public async Task<JsonElement> GetVersionAsync(string serverUrl)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, serverUrl.TrimEnd('/') + "/api/version");
        using var resp = await SendTrackedAsync(req);
        var data = await SendAsync(resp);
        if (data.TryGetProperty("minVersion", out var mv) && mv.ValueKind == JsonValueKind.String)
            ServerMinVersion = mv.GetString();
        return data;
    }

    public async Task<RegisterResult> RegisterDeviceAsync(string serverUrl, string code, string deviceName)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, serverUrl.TrimEnd('/') + "/api/device/register");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { code, name = deviceName, did = SettingsService.Instance.Values.HwId }),
            Encoding.UTF8, "application/json");
        using var resp = await SendTrackedAsync(req);
        var data = await SendAsync(resp);
        return new RegisterResult
        {
            Token = data.GetProperty("token").GetString() ?? "",
            DeviceId = data.GetProperty("deviceId").GetString() ?? "",
            ResetCode = data.TryGetProperty("resetCode", out var rc) ? rc.GetString() ?? "" : "",
        };
    }

    public async Task UnregisterDeviceAsync()
    {
        using var req = Req(HttpMethod.Post, "/api/device/unregister");
        using var resp = await SendTrackedAsync(req);
        await SendAsync(resp);
    }

    public sealed class DeviceResetCode { public string Username { get; set; } = ""; public string Code { get; set; } = ""; }

    /// <summary>为本机绑定教师生成一次性密码重置码（30 分钟有效）。持有设备即身份证明。</summary>
    public async Task<DeviceResetCode> GetDeviceResetCodeAsync()
    {
        using var resp = await SendTrackedAsync(Req(HttpMethod.Get, "/api/device/reset-codes"));
        var data = await SendAsync(resp);
        return new DeviceResetCode
        {
            Username = data.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
            Code = data.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
        };
    }

    /// <summary>匿名遥测心跳（每日一次；用户关闭则不调用）。</summary>
    public async Task TelemetryAsync(string kind, string version)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/api/telemetry");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { kind, version }),
            Encoding.UTF8, "application/json");
        using var resp = await SendTrackedAsync(req);
    }

    public async Task<JsonElement> GetDeviceInfoAsync()
    {
        using var resp = await SendTrackedAsync(Req(HttpMethod.Get, "/api/device/info"));
        return await SendAsync(resp);
    }

    public async Task HeartbeatAsync()
    {
        var s = SettingsService.Instance.Values;
        using var req = Req(HttpMethod.Post, "/api/device/heartbeat");
        // 能力位同步：WebUI 据此渲染功能锁态（未开启 → 手机端置灰并说明）
        req.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                version = LogService.AppVersion,
                caps = new
                {
                    camera = s.AllowCamera,
                    power = s.AllowPower,
                    clipboard = s.AllowClipboard,
                },
            }), Encoding.UTF8, "application/json");
        using var resp = await SendTrackedAsync(req);
        await SendAsync(resp);
    }

    public sealed class BroadcastItem
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string Level { get; set; } = "info";
        public long CreatedAt { get; set; }
    }

    /// <summary>拉取服务公告（公开接口；过期由服务端判定为 null）。</summary>
    public async Task<BroadcastItem?> GetBroadcastAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + "/api/broadcast");
        using var resp = await SendTrackedAsync(req);
        var data = await SendAsync(resp);
        if (!data.TryGetProperty("broadcast", out var b) || b.ValueKind != JsonValueKind.Object) return null;
        return new BroadcastItem
        {
            Id = b.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
            Text = b.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
            Level = b.TryGetProperty("level", out var l) ? l.GetString() ?? "info" : "info",
            CreatedAt = b.TryGetProperty("createdAt", out var c) ? c.GetInt64() : 0,
        };
    }

    public sealed class FeedbackResult { public bool Ok { get; set; } public string Error { get; set; } = ""; }

    /// <summary>提交用户反馈（≤500 字；可选附 ≤8KB 脱敏日志）。内容仅管理员可见。</summary>
    public async Task<(bool ok, string error)> SendFeedbackAsync(string message, string category, string? redactedLogs)
    {
        try
        {
            using var req = Req(HttpMethod.Post, "/api/feedback");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { message, category = category ?? "", logs = redactedLogs ?? "" }),
                Encoding.UTF8, "application/json");
            using var resp = await SendTrackedAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                string msg = $"服务器返回 {(int)resp.StatusCode}";
                try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("message", out var m)) msg = m.GetString() ?? msg; } catch { }
                return (false, msg);
            }
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(List<InboxMessage>, long)> GetMessagesAsync(long after, int wait)
    {
        using var resp = await SendTrackedAsync(Req(HttpMethod.Get, $"/api/device/messages?after={after}&wait={wait}"));
        var data = await SendAsync(resp);
        var (list, maxSeq) = ParseMessages(data);
        return (list, Math.Max(maxSeq, after));
    }

    /// <summary>上报消息回执（received/displayed/failed）。失败静默。</summary>
    public async Task SendReceiptAsync(string mid, string status)
    {
        try
        {
            using var req = Req(HttpMethod.Post, "/api/device/receipt");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { mid, status }), Encoding.UTF8, "application/json");
            using var resp = await SendTrackedAsync(req);
            await SendAsync(resp);
        }
        catch (Exception ex) { App.Log.Aggregate("回执上报", ex); }
    }

    public async Task<UpdateManifest?> GetUpdateLatestAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + "/api/update/latest");
        using var resp = await SendTrackedAsync(req);
        var data = await SendAsync(resp);
        if (data.TryGetProperty("version", out var v) && v.GetString() is string ver && ver != "none")
            return new UpdateManifest
            {
                Version = ver,
                Url = data.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                Sha256 = data.TryGetProperty("sha256", out var sh) ? sh.GetString() ?? "" : "",
                Notes = data.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "",
            };
        return null;
    }

    /// <summary>把教室电脑剪贴板文本推送给教师（WebUI 端显示）。</summary>
    public async Task PushClipboardToTeacherAsync(string text)
    {
        using var req = Req(HttpMethod.Post, "/api/device/clipboard");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json");
        using var resp = await SendTrackedAsync(req);
        await SendAsync(resp);
    }

    /// <summary>拉取教师常驻通知（灵动岛）。</summary>
    public async Task<List<(string name, string text)>> GetDeviceNoticesAsync()
    {
        using var resp = await SendTrackedAsync(Req(HttpMethod.Get, "/api/device/notices"));
        var data = await SendAsync(resp);
        var list = new List<(string, string)>();
        if (data.TryGetProperty("notices", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var n in arr.EnumerateArray())
                list.Add((n.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                          n.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : ""));
        return list;
    }

    /// <summary>设备照片回传：multipart 上传到教师文件列表。</summary>
    public async Task UploadToTeacherAsync(string filePath, string fileName)
    {
        using var content = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        // workerd formData() rejects .NET default disposition (unquoted name/filename); build browser-style manually.
        // 真实文件名经独立 name 字段携带（workerd 会把非 ASCII filename 换成 ?）
        var asciiName = "upload-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + Path.GetExtension(fileName);
        var cd = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data");
        cd.Name = "\"file\"";
        cd.FileName = "\"" + asciiName + "\"";
        fileContent.Headers.ContentDisposition = cd;
        content.Add(fileContent);
        var nameField = new StringContent(fileName);
        nameField.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data");
        nameField.Headers.ContentDisposition.Name = "\"name\"";
        content.Add(nameField);
        using var req = Req(HttpMethod.Post, "/api/device/upload");
        req.Content = content;
        using var resp = await SendTrackedAsync(req);
        await SendAsync(resp);
    }

    /// <summary>上报早读看板状态快照（WebUI 端读取）。</summary>
    public async Task PublishReadBoardStateAsync(string snapshotJson)
    {
        using var req = Req(HttpMethod.Post, "/api/device/readboard-state");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { snapshot = snapshotJson }), Encoding.UTF8, "application/json");
        using var resp = await SendTrackedAsync(req);
        await SendAsync(resp);
    }

    public sealed class SubjectTeacher { public string Uid { get; set; } = ""; public string Username { get; set; } = ""; public string Subject { get; set; } = ""; public string Avatar { get; set; } = ""; }

    public sealed class TeacherInfo { public string Username { get; set; } = ""; public string Avatar { get; set; } = ""; public string Greeting { get; set; } = ""; }

    /// <summary>按用户名查教师公开信息（看板问候行用）。</summary>
    public async Task<TeacherInfo?> GetTeacherInfoAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        using var resp = await SendTrackedAsync(Req(HttpMethod.Get,
            "/api/device/teacher-info?name=" + Uri.EscapeDataString(username)));
        if (!resp.IsSuccessStatusCode) return null;
        var data = await SendAsync(resp);
        return new TeacherInfo
        {
            Username = data.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
            Avatar = data.TryGetProperty("avatar", out var a) ? a.GetString() ?? "" : "",
            Greeting = data.TryGetProperty("greeting", out var g) ? g.GetString() ?? "" : "",
        };
    }

    public sealed class ReadBoardConfig { public string Mode { get; set; } = "slash"; public int Ratio { get; set; } = 3; }

    /// <summary>拉取早读看板账号级配置（加分模式 + 斜杠/红花换算比例）。</summary>
    public async Task<ReadBoardConfig> GetReadBoardConfigAsync()
    {
        using var resp = await SendTrackedAsync(Req(HttpMethod.Get, "/api/device/readboard-config"));
        var data = await SendAsync(resp);
        var cfg = new ReadBoardConfig();
        if (data.TryGetProperty("config", out var c))
        {
            if (c.TryGetProperty("mode", out var m)) cfg.Mode = m.GetString() ?? "slash";
            if (c.TryGetProperty("ratio", out var r)) cfg.Ratio = r.GetInt32();
        }
        return cfg;
    }

    public async Task<SmartPreclassService.TimetableDoc?> GetDeviceTimetableAsync()
    {
        using var resp = await SendTrackedAsync(Req(HttpMethod.Get, "/api/device/timetable"));
        var data = await SendAsync(resp);
        if (!data.TryGetProperty("timetable", out var tt) || tt.ValueKind != JsonValueKind.Object) return null;
        var doc = new SmartPreclassService.TimetableDoc
        {
            PreclassMinutes = tt.TryGetProperty("preclassMinutes", out var pm) ? pm.GetInt32() : 5,
        };
        if (tt.TryGetProperty("periods", out var periods))
            foreach (var el in periods.EnumerateArray())
                doc.Periods.Add(new SmartPreclassService.Period
                {
                    P = el.TryGetProperty("p", out var pv) ? pv.GetInt32() : 0,
                    Start = el.TryGetProperty("start", out var ps) ? ps.GetString() ?? "08:00" : "08:00",
                    End = el.TryGetProperty("end", out var pe) ? pe.GetString() ?? "" : "",
                });
        if (tt.TryGetProperty("lessons", out var lessons))
            foreach (var el in lessons.EnumerateArray())
                doc.Lessons.Add(new SmartPreclassService.Lesson
                {
                    Day = el.TryGetProperty("day", out var dv) ? dv.GetInt32() : 1,
                    Period = el.TryGetProperty("period", out var pv2) ? pv2.GetInt32() : 1,
                    Subject = el.TryGetProperty("subject", out var sj) ? sj.GetString() ?? "" : "",
                });
        if (tt.TryGetProperty("overrides", out var ovs) && ovs.ValueKind == JsonValueKind.Object)
        {
            foreach (var dayEntry in ovs.EnumerateObject())
            {
                var list = new List<SmartPreclassService.Lesson>();
                if (dayEntry.Value.ValueKind == JsonValueKind.Array)
                    foreach (var el in dayEntry.Value.EnumerateArray())
                        list.Add(new SmartPreclassService.Lesson
                        {
                            Period = el.TryGetProperty("period", out var pv3) ? pv3.GetInt32() : 0,
                            Subject = el.TryGetProperty("subject", out var sj) ? sj.GetString() ?? "" : "",
                        });
                doc.Overrides[dayEntry.Name] = list;
            }
        }
        return doc.Lessons.Count > 0 || doc.Periods.Count > 0 ? doc : null;
    }

    public async Task<SubjectTeacher?> GetSubjectTeacherAsync(string subject)
    {
        using var resp = await SendTrackedAsync(Req(HttpMethod.Get,
            "/api/device/subject-teacher?subject=" + Uri.EscapeDataString(subject)));
        if (!resp.IsSuccessStatusCode) return null;
        var data = await SendAsync(resp);
        return new SubjectTeacher
        {
            Uid = data.TryGetProperty("uid", out var u) ? u.GetString() ?? "" : "",
            Username = data.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "",
            Subject = data.TryGetProperty("subject", out var sj) ? sj.GetString() ?? "" : "",
            Avatar = data.TryGetProperty("avatar", out var av) ? av.GetString() ?? "" : "",
        };
    }

    public async Task<List<SmartPreclassService.ActionItem>> GetActionSetByUidAsync(string uid)
    {
        var list = new List<SmartPreclassService.ActionItem>();
        using var resp = await SendTrackedAsync(Req(HttpMethod.Get,
            "/api/device/action-set?uid=" + Uri.EscapeDataString(uid)));
        if (!resp.IsSuccessStatusCode) return list;
        var data = await SendAsync(resp);
        if (data.TryGetProperty("actions", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
                list.Add(new SmartPreclassService.ActionItem
                {
                    Type = a.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                    Param = a.TryGetProperty("param", out var p) ? p.GetString() ?? "" : "",
                });
        return list;
    }

    public async Task AckAsync(long after)
    {
        try
        {
            using var req = Req(HttpMethod.Post, "/api/device/ack");
            req.Content = new StringContent(JsonSerializer.Serialize(new { after }), Encoding.UTF8, "application/json");
            using var resp = await SendTrackedAsync(req);
            await SendAsync(resp);
        }
        catch (Exception ex) { App.Log.Error("ACK 失败（忽略）", ex); }
    }

    /// <summary>下载文件到指定路径（含进度回调 0-100）。</summary>
    public async Task DownloadFileAsync(string fid, string destPath, Action<int>? progress = null)
    {
        using var resp = await SendTrackedAsync(Req(HttpMethod.Get, "/api/device/files/" + fid),
            HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(destPath);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        int lastPct = -1;
        while ((n = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total > 0)
            {
                int pct = (int)(read * 100 / total);
                if (pct != lastPct) { lastPct = pct; progress?.Invoke(pct); }
            }
        }
    }
}

public sealed class RegisterResult
{
    public string Token { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string ResetCode { get; set; } = "";
}

/// <summary>扫码绑定请求已过期（10 分钟）。</summary>
public sealed class PairRequestExpiredException : Exception
{
    public PairRequestExpiredException() : base("绑定请求已过期，请重新生成二维码") { }
}

public partial class ApiService
{
    /// <summary>
    /// 发起一次性扫码绑定请求，返回 reqId（10 分钟有效）。已注册设备携带 did，扫码教师将关联到同一台设备。
    /// 网络失败自动重试 ×3（含备用域名切换）。
    /// </summary>
    public async Task<string> CreatePairRequestAsync(string deviceName, string? existingDeviceId = null)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/api/device/pair-request");
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { name = deviceName, did = string.IsNullOrEmpty(existingDeviceId) ? null : existingDeviceId }),
                    Encoding.UTF8, "application/json");
                using var resp = await SendTrackedAsync(req);
                var data = await SendAsync(resp);
                return data.GetProperty("reqId").GetString() ?? "";
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                App.Log.Aggregate("扫码绑定", ex);
                await Task.Delay(800 * (attempt + 1));
            }
        }
        throw last ?? new HttpRequestException("绑定请求失败");
    }

    /// <summary>
    /// 等待教师扫码确认绑定。返回 null 表示仍在等待；
    /// 新设备返回令牌；已注册设备（多教师共享）返回空令牌 + 原 did。
    /// </summary>
    public async Task<RegisterResult?> PairWaitAsync(string reqId, int wait, CancellationToken ct)
    {
        using var resp = await SendTrackedAsync(
            Req(HttpMethod.Get, $"/api/device/pair-wait?req={Uri.EscapeDataString(reqId)}&wait={wait}"));
        if (resp.StatusCode == System.Net.HttpStatusCode.Gone)
            throw new PairRequestExpiredException();
        if (!resp.IsSuccessStatusCode) return null; // 视为继续等待
        var data = await SendAsync(resp);
        if (data.TryGetProperty("pending", out var pending) && pending.GetBoolean()) return null;
        return new RegisterResult
        {
            Token = data.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "",
            DeviceId = data.GetProperty("did").GetString() ?? "",
            DeviceName = data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
        };
    }
}
