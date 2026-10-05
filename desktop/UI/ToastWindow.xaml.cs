using System.Windows;
using System.Windows.Threading;

namespace Classify.UI;

/// <summary>右下角 Toast 提示（不抢焦点，6 秒自动消失，高度随内容自适应）。</summary>
public partial class ToastWindow : Window
{
    public static ToastWindow? Current { get; private set; }

    private const double WidthDip = 380;
    private DispatcherTimer? _closeTimer;

    public ToastWindow()
    {
        InitializeComponent();
        Current = this;
        WindowUtil.ConfigureFloating(this);
        Width = WidthDip;
        Hide();
    }

    public static void ShowToast(string title, string body)
    {
        // 可能从后台线程调用（如文件接收完成回调），先归队到 UI 线程
        var app = System.Windows.Application.Current;
        if (app == null) return;
        var disp = app.Dispatcher;
        if (!disp.CheckAccess())
        {
            disp.BeginInvoke(new Action(() => ShowToast(title, body)));
            return;
        }
        if (Current is null) Current = new ToastWindow();
        Current.ShowInternal(title, body);
    }

    private void ShowInternal(string title, string body)
    {
        TitleText.Text = title;
        BodyText.Text = body;
        PositionBottomRight();
        WindowUtil.ShowWithoutActivate(this);

        _closeTimer?.Stop();
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _closeTimer.Tick += (_, _) => { _closeTimer?.Stop(); Hide(); };
        _closeTimer.Start();
    }

    private void PositionBottomRight()
    {
        var dpi = WindowUtil.GetDpiForHwnd(WindowUtil.Hwnd(this));
        var b = Interop.Win32.MonitorBoundsOfWindow(WindowUtil.Hwnd(this));
        var wa = Interop.Win32.WorkAreaOfHwnd(WindowUtil.Hwnd(this));
        var heightDip = ActualHeight > 0 ? ActualHeight : 80;
        Left = (wa.Right - WidthDip * dpi - 20 * dpi) / dpi;
        Top = (wa.Bottom - heightDip * dpi - 20 * dpi) / dpi;
        _ = b;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (Current == this) Current = null;
        base.OnClosed(e);
    }
}
