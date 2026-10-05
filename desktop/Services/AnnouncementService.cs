using System.ComponentModel;
using System.Media;
using System.Windows.Threading;
using Classify.Models;
using Classify.UI;

namespace Classify.Services;

/// <summary>
/// 喊话编排：队列化处理每条喊话。
/// 未上课 → 全屏大屏展示；正在上课（全屏课件）→ 顶部弹窗，不抢焦点。
/// 朗读与展示并行，展示时长取「朗读完成 + 2 秒」与「最短展示时间」的较大者。
/// </summary>
public sealed class AnnouncementService
{
    public static AnnouncementService Instance { get; } = new();

    private readonly Queue<AnnItem> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private bool _running;

    public void Init()
    {
        if (_running) return;
        _running = true;
        _ = Task.Run(ProcessLoop);
    }

    public void Enqueue(AnnItem item)
    {
        lock (_gate) _queue.Enqueue(item);
        _signal.Release();
    }

    private async Task ProcessLoop()
    {
        while (true)
        {
            await _signal.WaitAsync();
            AnnItem item;
            lock (_gate) item = _queue.Dequeue();
            try { await ShowAsync(item); }
            catch (Exception ex) { App.Log.Error("展示喊话失败", ex); }
        }
    }

    private async Task ShowAsync(AnnItem item)
    {
        var settings = SettingsService.Instance.Values;
        bool inClass = ClassDetectService.Instance.InClass;
        bool dnd = DndActive();
        if (dnd) App.Log.Info("勿扰模式：本次喊话只展示，不朗读不出提示音");
        App.Log.Info($"展示喊话 mode={(inClass ? "popup" : "overlay")} speak={item.Speak}");

        ReceiptService.Send(item.Mid, "received");

        // 前置提示音：先出窗口再放音（异步），不让提示音阻塞展示（勿扰时跳过）
        Task chime = settings.ChimeEnabled && !dnd ? Task.Run(PlayChime) : Task.CompletedTask;

        // 展示窗口（UI 线程）；ShowAnnouncement 返回的 Task 在用户点击关闭时完成
        Task userClosed = Task.CompletedTask;
        await ShowOnUi(() =>
        {
            userClosed = inClass
                ? ClassPopupWindow.ShowAnnouncement(item)
                : OverlayWindow.ShowAnnouncement(item);
        });
        ReceiptService.Send(item.Mid, "displayed");

        // 提示音结束后再开始朗读，避免人声与音效重叠（提示音最长等 2.5 秒）
        await Task.WhenAny(chime, Task.Delay(2500));

        // 朗读（勿扰时跳过）
        Task tts = item.Speak && settings.TtsEnabled && !dnd
            ? TtsService.Instance.SpeakAsync(item.Text)
            : Task.CompletedTask;

        // Ducking：朗读期间压低【其他应用】的会话音量（Classify 朗读保持最大声），结束后恢复
        bool ducked = false;
        if (settings.DuckingEnabled && item.Speak && settings.TtsEnabled && !dnd)
            ducked = Interop.AudioSessions.DuckOthers(0.15f);

        try
        {
            int minSeconds = inClass ? settings.PopupSeconds : settings.OverlaySeconds;
            var minDelay = Task.Delay(TimeSpan.FromSeconds(minSeconds));

            // 展示结束条件：用户关闭，或（朗读完成 且 已到最短展示时间）取先到者
            var displayEnd = Task.WhenAll(minDelay, tts);
            var first = await Task.WhenAny(userClosed, displayEnd);
            if (first != userClosed)
            {
                var grace = Task.Delay(TimeSpan.FromSeconds(2));
                await Task.WhenAny(userClosed, grace);
            }

            await ShowOnUi(() =>
            {
                if (inClass) ClassPopupWindow.Dismiss();
                else OverlayWindow.Dismiss();
            });
            if (!tts.IsCompleted) TtsService.Instance.Stop();
        }
        finally
        {
            if (ducked) Interop.AudioSessions.Restore(); // 异常路径也必须恢复各应用音量
        }
    }

    /// <summary>勿扰模式是否生效（考试/放映场景：喊话只展示不发声）。</summary>
    public static bool DndActive() =>
        SettingsService.Instance.Values.DndUntil > DateTime.Now;

    /// <summary>切换勿扰模式。minutes=0 立即结束。</summary>
    public static void SetDnd(int minutes)
    {
        var s = SettingsService.Instance.Values;
        s.DndUntil = minutes > 0 ? DateTime.Now.AddMinutes(minutes) : DateTime.MinValue;
        SettingsService.Instance.Save();
        ToastWindow.ShowToast(minutes > 0 ? "🌙 勿扰模式已开启" : "勿扰模式已结束",
            minutes > 0
                ? $"接下来 {minutes} 分钟：喊话只展示，不朗读不出提示音"
                : "喊话恢复正常朗读与提示音");
        App.Log.Info($"勿扰模式 {(minutes > 0 ? $"开启 {minutes} 分钟" : "关闭")}");
    }

    private static Task ShowOnUi(Action show)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        App.Ui.BeginInvoke(new Action(() =>
        {
            try { show(); }
            catch (Exception ex) { App.Log.Error("窗口展示失败", ex); }
            tcs.SetResult(null);
        }));
        return tcs.Task;
    }

    private static void PlayChime()
    {
        try
        {
            // 优先：用户自定义提示音 → 内置 chime.wav → 系统音
            string[] candidates =
            {
                SettingsService.Instance.Values.ChimePath ?? "",
                Path.Combine(AppContext.BaseDirectory, "Assets", "chime.wav"),
                @"C:\Windows\Media\Windows Notify System Generic.wav",
                @"C:\Windows\Media\chimes.wav",
                @"C:\Windows\Media\notify.wav",
            };
            var file = candidates.FirstOrDefault(f => f.Length > 0 && File.Exists(f));
            if (file == null) return;
            using var player = new SoundPlayer(file);
            player.PlaySync(); // 短提示音，同步播放无妨
        }
        catch { }
    }
}
