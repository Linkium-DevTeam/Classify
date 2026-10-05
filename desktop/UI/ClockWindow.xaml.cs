using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Classify.Services;

namespace Classify.UI;

/// <summary>
/// 全屏时钟：时钟模式 / 倒计时模式，教师手机端远程启停。
/// 1Hz 刷新、常驻 CPU 近乎为零；Esc 或点击退出。倒计时结束红闪 + 语音「时间到」。
/// </summary>
public partial class ClockWindow : Window
{
    public static ClockWindow? Current { get; private set; }

    private DispatcherTimer? _timer;
    private DispatcherTimer? _topTimer;
    private DateTimeOffset? _countdownEnd;
    private string _countdownLabel = "";
    private bool _endAnnounced;
    private static readonly Brush BgNormal = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x20));
    private static readonly Brush BgFlash = new SolidColorBrush(Color.FromRgb(0x96, 0x18, 0x18));

    public ClockWindow()
    {
        InitializeComponent();
        Current = this;
    }

    public static void StartClock()
    {
        if (Current is null) Current = new ClockWindow();
        Current.ShowInternal();
    }

    public static void StopClock()
    {
        if (Current is { } w)
        {
            w._timer?.Stop();
            w.Close();
            Current = null;
        }
    }

    private void ShowInternal()
    {
        WindowUtil.EnterFullScreen(this);
        if (!IsLoaded)
        {
            Show();
            Root.Focus();
        }
        else
        {
            WindowUtil.ShowWithoutActivate(this);
        }
        Wallpaper.Apply(WallpaperImg, opacity: 0.35, blurRadius: 34);
        StartTimer();
        // 置顶看门狗：同 ReadBoardWindow，防全屏课件窗口把时钟压下去
        if (_topTimer is null)
        {
            _topTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _topTimer.Tick += (_, _) => { if (IsVisible) WindowUtil.EnsureTopmost(this); };
        }
        _topTimer.Start();
    }

    private void StartTimer()
    {
        UpdateTick();
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdateTick();
        _timer.Start();
    }

    private void UpdateTick()
    {
        var now = DateTime.Now;

        if (_countdownEnd is { } end)
        {
            // 倒计时模式：大数字让位给剩余时间，醒目分级配色
            var left = end - DateTimeOffset.Now;
            bool ended = left <= TimeSpan.Zero;
            bool ending = !ended && left <= TimeSpan.FromSeconds(10);

            TimeText.Text = ended ? "00:00:00"
                : $"{(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}";
            DateText.Text = now.ToString("yyyy年M月d日 dddd");
            ModeText.Text = ended ? $"{_countdownLabel} · ⏰ 时间到！" : $"{_countdownLabel} · 剩余时间";
            TimeText.FontWeight = FontWeights.Bold;
            TimeText.Foreground = ended ? Brushes.Red
                : ending ? Brushes.Orange
                : new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));

            if (ended && !_endAnnounced)
            {
                _endAnnounced = true;
                AnnounceEnd();
            }
        }
        else
        {
            TimeText.Text = now.ToString("HH:mm:ss");
            DateText.Text = now.ToString("yyyy年M月d日 dddd");
            ModeText.Text = _countdownLabel;
            TimeText.FontWeight = FontWeights.Light;
            TimeText.Foreground = Brushes.White;
        }
    }

    /// <summary>倒计时结束反馈：红光闪三下 + 语音播报「时间到」。</summary>
    private void AnnounceEnd()
    {
        try
        {
            var flash = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            int n = 0;
            flash.Tick += (_, _) =>
            {
                n++;
                Root.Background = n % 2 == 1 ? BgFlash : BgNormal;
                if (n >= 6)
                {
                    flash.Stop();
                    Root.Background = BgNormal;
                }
            };
            flash.Start();
        }
        catch { }
        _ = TtsService.Instance.SpeakAsync("倒计时时间到。");
    }

    /// <summary>设置倒计时（分钟）；0 表示纯时钟。</summary>
    public void SetCountdown(int minutes, string label)
    {
        _countdownLabel = label;
        _countdownEnd = minutes > 0 ? DateTimeOffset.Now.AddMinutes(minutes) : null;
        _endAnnounced = false;
        UpdateTick();
    }

    private void Root_Tapped(object sender, MouseButtonEventArgs e) => StopClock();

    private void Root_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) StopClock();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer?.Stop();
        _topTimer?.Stop();
        if (Current == this) Current = null;
        base.OnClosed(e);
    }
}
