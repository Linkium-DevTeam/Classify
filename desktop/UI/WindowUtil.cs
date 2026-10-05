using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Classify.Interop;

namespace Classify.UI;

/// <summary>WPF 窗口工具：全屏、无边框免激活悬浮、置顶、DWM 圆角、深色标题栏。</summary>
public static class WindowUtil
{
    public static IntPtr Hwnd(Window w) => new WindowInteropHelper(w).EnsureHandle();

    /// <summary>Win11 圆角（Win10 自动忽略）。无边框窗口的圆角必须交给 DWM，自绘圆角必然露角。</summary>
    public static void RoundCorners(Window w) => Win32.RoundCorners(Hwnd(w));

    /// <summary>深色标题栏（Win10 1809+）。</summary>
    public static void DarkTitleBar(Window w)
    {
        try
        {
            var hwnd = Hwnd(w);
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref on, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>真·全屏（覆盖任务栏）：无边框 + 按物理像素铺满窗口所在显示器 + 置顶。
    /// 不经 WPF 的 DIP Left/Top 换算（窗口显示前 TransformToDevice 可能给出错误 scale，
    /// 曾导致窗口比屏幕宽 25%、内容偏右）——直接 SetWindowPos 物理像素落位。</summary>
    public static void EnterFullScreen(Window w)
    {
        w.WindowStyle = WindowStyle.None;
        w.ResizeMode = ResizeMode.NoResize;
        w.ShowInTaskbar = false;
        // 确保句柄与窗口宿主存在（WPF 才会跟随 Win32 尺寸变化）
        var hwnd = Hwnd(w);
        if (!w.IsVisible) w.Show();
        var b = Win32.MonitorBoundsOfWindow(hwnd);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST,
            b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top,
            Win32.SWP_SHOWWINDOW | Win32.SWP_NOACTIVATE);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    public static double GetDpiForHwnd(IntPtr hwnd) => Interop.Win32.DpiOfWindow(hwnd);

    public static void SetTopmost(Window w)
    {
        Win32.SetWindowPos(Hwnd(w), Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 置顶看门狗用：重申 TOPMOST 标志（防 WPF/系统清掉），再在 topmost 层内抬到最上
    /// （同层窗口后弹者胜——歌词条/全屏课件等同为 topmost 时，仅重申标志压不过它们）。
    /// </summary>
    public static void EnsureTopmost(Window w)
    {
        var hwnd = Hwnd(w);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOP, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>不抢焦点显示。</summary>
    public static void ShowWithoutActivate(Window w)
    {
        w.Show();
        SetTopmost(w);
    }

    /// <summary>无边框置顶免激活悬浮窗（上课弹窗 / Toast / 灵动岛）。调用方自行设置尺寸与位置。</summary>
    public static void ConfigureFloating(Window w)
    {
        w.WindowStyle = WindowStyle.None;
        w.ResizeMode = ResizeMode.NoResize;
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        var hwnd = Hwnd(w);
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE,
            new IntPtr(style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST));
        RoundCorners(w);
    }
}
