using System.Windows.Threading;
using Classify.UI;

namespace Classify.Services;

/// <summary>
/// 服务公告：定期拉取公开 /api/broadcast，新公告（id 未见过）弹托盘气泡；
/// 紧急级别同时弹应用内 Toast。公告由管理员在 WebUI 发布（维护通知/故障通报/新版本）。
/// </summary>
public static class BroadcastService
{
    private static readonly ApiService Api = new();
    private static CancellationTokenSource? _cts;

    public static void Start()
    {
        if (!SettingsService.Instance.Values.WizardDone) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            await CheckOnceAsync(); // 启动即查一次，不空等
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(30), _cts.Token); } catch { return; }
                try { await CheckOnceAsync(); } catch (Exception ex) { App.Log.Aggregate("服务公告", ex); }
            }
        });
    }

    public static void Stop()
    {
        _cts?.Cancel();
    }

    private static async Task CheckOnceAsync()
    {
        var b = await Api.GetBroadcastAsync();
        if (b is null || string.IsNullOrEmpty(b.Id)) return;
        var s = SettingsService.Instance.Values;
        bool isNew = s.LastBroadcastId != b.Id;
        if (isNew)
        {
            s.LastBroadcastId = b.Id;
            SettingsService.Instance.Save();
            App.Log.Info($"服务公告[{b.Level}] {b.Text}");
            TrayService.Instance.ShowBalloon(
                b.Level == "critical" ? "🔴 服务公告（紧急）" : b.Level == "warn" ? "🟠 服务公告" : "📢 服务公告",
                b.Text);
            if (b.Level == "critical")
            {
                _ = App.Ui.BeginInvoke(new Action(() =>
                    ToastWindow.ShowToast("🔴 服务公告", b.Text)));
            }
        }
    }
}
