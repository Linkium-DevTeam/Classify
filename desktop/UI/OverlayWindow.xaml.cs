using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Classify.Models;
using Classify.Services;

namespace Classify.UI;

/// <summary>全屏喊话大屏（未上课时使用）：无边框铺满主显示器并置顶。</summary>
public partial class OverlayWindow : Window
{
    public static OverlayWindow? Current { get; private set; }

    private TaskCompletionSource _userClosed = CreateTcs();
    private DispatcherTimer? _countdown;
    private int _secondsLeft;
    private bool _everShown;

    public OverlayWindow()
    {
        InitializeComponent();
        Current = this;
        TtsService.Instance.PropertyChanged += OnTtsChanged;
    }

    private static TaskCompletionSource CreateTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task ShowAnnouncement(AnnItem item)
    {
        if (Current is null) Current = new OverlayWindow();
        return Current.ShowInternal(item);
    }

    public static void Dismiss()
    {
        if (Current is { } w)
        {
            w._countdown?.Stop();
            w.Hide();
        }
    }

    private Task ShowInternal(AnnItem item)
    {
        MainText.Text = string.IsNullOrWhiteSpace(item.Text) ? " " : item.Text;
        MainText.FontSize = item.Text.Length > 120 ? 38 : item.Text.Length > 50 ? 48 : 56;
        MainText.LineHeight = MainText.FontSize * 1.35;
        FromText.Text = string.IsNullOrWhiteSpace(item.From) ? "课堂通知" : $"来自 · {item.From}";
        _secondsLeft = Math.Max(8, SettingsService.Instance.Values.OverlaySeconds);
        HintText.Text = $"点击任意位置关闭 · {_secondsLeft} 秒后自动收起";

        Wallpaper.Apply(WallpaperImg, opacity: 0.35, blurRadius: 34);

        _userClosed = CreateTcs();
        WindowUtil.EnterFullScreen(this);
        if (!_everShown)
        {
            Show();
            _everShown = true;
        }
        else
        {
            WindowUtil.ShowWithoutActivate(this);
        }
        StartCountdown();
        UpdateSpeaking();
        return _userClosed.Task;
    }

    private void StartCountdown()
    {
        _countdown?.Stop();
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int tick = 0;
        _countdown.Tick += (_, _) =>
        {
            tick++;
            HintText.Text = $"点击任意位置关闭 · {Math.Max(0, _secondsLeft - tick)} 秒后自动收起";
        };
        _countdown.Start();
    }

    private void OnTtsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TtsService.Speaking))
            Dispatcher.BeginInvoke(new Action(UpdateSpeaking));
    }

    private void UpdateSpeaking()
    {
        SpeakingPanel.Visibility = TtsService.Instance.Speaking ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Root_Tapped(object sender, System.Windows.Input.MouseButtonEventArgs e) => CloseByUser();

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => CloseByUser();

    private void CloseByUser()
    {
        _countdown?.Stop();
        _userClosed.TrySetResult();
    }

    protected override void OnClosed(EventArgs e)
    {
        TtsService.Instance.PropertyChanged -= OnTtsChanged;
        if (Current == this) Current = null;
        base.OnClosed(e);
    }
}
