using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Classify.Models;
using Classify.Services;

namespace Classify.UI;

/// <summary>上课模式弹窗：顶部居中、不抢焦点、自动收起，避免打断全屏课件。</summary>
public partial class ClassPopupWindow : Window
{
    public static ClassPopupWindow? Current { get; private set; }

    private const double WidthDip = 720;
    private const double TopOffsetDip = 18;

    private TaskCompletionSource _userClosed = CreateTcs();
    private DispatcherTimer? _countdown;

    public ClassPopupWindow()
    {
        InitializeComponent();
        Current = this;
        WindowUtil.ConfigureFloating(this);
        Width = WidthDip;
        // 先构建一次内容再隐藏（WPF 无需 WinUI 的"先激活再隐藏"仪式）
        Hide();
        TtsService.Instance.PropertyChanged += OnTtsChanged;
    }

    private static TaskCompletionSource CreateTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task ShowAnnouncement(AnnItem item)
    {
        if (Current is null) Current = new ClassPopupWindow();
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
        MainText.FontSize = item.Text.Length > 90 ? 24 : item.Text.Length > 40 ? 28 : 32;
        MainText.LineHeight = MainText.FontSize * 1.35;
        FromText.Text = string.IsNullOrWhiteSpace(item.From) ? "课堂通知" : $"来自 · {item.From}";
        int seconds = Math.Max(6, SettingsService.Instance.Values.PopupSeconds);
        HintText.Text = $"{seconds} 秒后自动收起";

        _userClosed = CreateTcs();
        PositionTopCenter();
        WindowUtil.ShowWithoutActivate(this);

        StartCountdown(seconds);
        UpdateSpeaking();
        return _userClosed.Task;
    }

    private void PositionTopCenter()
    {
        var dpi = WindowUtil.GetDpiForHwnd(WindowUtil.Hwnd(this));
        var b = Interop.Win32.MonitorBoundsOfWindow(WindowUtil.Hwnd(this));
        Left = (b.Left + (b.Right - b.Left - WidthDip * dpi) / 2) / dpi;
        Top = (b.Top + TopOffsetDip * dpi) / dpi;
    }

    private void StartCountdown(int seconds)
    {
        _countdown?.Stop();
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int left = seconds;
        _countdown.Tick += (_, _) =>
        {
            left--;
            if (left <= 0)
            {
                _countdown?.Stop();
                Hide();
                _userClosed.TrySetResult();
            }
            else HintText.Text = $"{left} 秒后自动收起";
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

    private void Root_Tapped(object sender, MouseButtonEventArgs e) => CloseByUser();

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
