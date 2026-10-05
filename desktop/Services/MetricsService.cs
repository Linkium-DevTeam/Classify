using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Classify.Services;

/// <summary>
/// 匿名遥测（定量不采内容）：功能事件本地计数、每日聚合上报一次；
/// 崩溃检测：运行标记文件残留 = 上次非正常退出 → 上报脱敏日志尾部（凭据已清除）。
/// 红线：喊话文本/文件名/剪贴板/学生姓名永不进入遥测；用户可在设置关闭总开关。
/// </summary>
public static class MetricsService
{
    private static readonly ConcurrentDictionary<string, int> Counters = new();
    private static CancellationTokenSource? _cts;

    public static void Incr(string name, int n = 1) => Counters.AddOrUpdate(name, n, (_, v) => v + n);

    public static void Start()
    {
        if (!SettingsService.Instance.Values.WizardDone) return;

        // 崩溃检测：标记残留 = 上次非正常退出
        var flag = Path.Combine(AppPaths.DataDir, "running.flag");
        var crashDetected = File.Exists(flag);
        try
        {
            File.WriteAllText(flag, Environment.ProcessId.ToString());
        }
        catch { }

        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            if (crashDetected)
            {
                App.Log.Info("检测到上次非正常退出，上报脱敏崩溃日志");
                await UploadCrashAsync();
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), _cts.Token); } catch { return; }
            await FlushAsync();
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromHours(24), _cts.Token); } catch { return; }
                await FlushAsync();
            }
        });
    }

    public static void Stop() => _cts?.Cancel();

    /// <summary>计划内退出前调用：移除运行标记，避免误报崩溃。</summary>
    public static void MarkCleanExit()
    {
        try { File.Delete(Path.Combine(AppPaths.DataDir, "running.flag")); } catch { }
    }

    /// <summary>把本地计数聚合成一次心跳上报（每账号每天至多几次 KV 写，免费额度纪律）。</summary>
    public static async Task FlushAsync()
    {
        try
        {
            if (!SettingsService.Instance.Values.TelemetryEnabled) return;
            var events = Counters.ToArray()
                .Select(kv => new { name = kv.Key, count = kv.Value })
                .ToArray();
            if (events.Length == 0) return;
            Counters.Clear();

            using var req = new System.Net.Http.HttpRequestMessage(
                HttpMethod.Post, ServerEndpoints.Current + "/api/telemetry");
            req.Content = new System.Net.Http.StringContent(
                JsonSerializer.Serialize(new { kind = "app", version = LogService.AppVersion, events }),
                Encoding.UTF8, "application/json");
            using var resp = await new System.Net.Http.HttpClient().SendAsync(req);
            App.Log.Info($"遥测心跳已上报（{events.Length} 项计数）");
        }
        catch (Exception ex) { App.Log.Aggregate("遥测", ex); }
    }

    private static async Task UploadCrashAsync()
    {
        try
        {
            if (!SettingsService.Instance.Values.TelemetryEnabled) return;
            using var req = new System.Net.Http.HttpRequestMessage(
                HttpMethod.Post, ServerEndpoints.Current + "/api/telemetry");
            req.Content = new System.Net.Http.StringContent(
                JsonSerializer.Serialize(new
                {
                    kind = "crash",
                    version = LogService.AppVersion,
                    detail = App.Log.RedactedTail(2000),
                }),
                Encoding.UTF8, "application/json");
            using var resp = await new System.Net.Http.HttpClient().SendAsync(req);
        }
        catch (Exception ex) { App.Log.Aggregate("崩溃上报", ex); }
    }
}
