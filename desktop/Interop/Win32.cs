using System.Runtime.InteropServices;

namespace Classify.Interop;

public static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const long WS_EX_TOPMOST = 0x00000008L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_TOP = new(0);
    public const uint WM_SYSCOMMAND = 0x0112;
    public const int SC_MONITORPOWER = 0xF170;
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags,
        System.Text.StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Win11 圆角（Win10 自动忽略）。无边框窗口的圆角必须交给 DWM，自绘圆角必然露角。</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        try
        {
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>窗口所在显示器的完整边界（非工作区）。</summary>
    public static RECT MonitorBoundsOfWindow(IntPtr hwnd)
    {
        var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (mon != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi)) return mi.rcMonitor;
        }
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    /// <summary>窗口所在显示器的工作区（扣除任务栏）。</summary>
    public static RECT WorkAreaOfHwnd(IntPtr hwnd)
    {
        var mon = MonitorFromWindow(hwnd, 2);
        if (mon != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi)) return mi.rcWork;
        }
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1032 };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>窗口所在显示器的实时 DPI 缩放（1.0 / 1.25 / 1.5…）。比 HwndSource 显示前取值可靠。</summary>
    public static double DpiOfWindow(IntPtr hwnd)
    {
        try
        {
            var d = GetDpiForWindow(hwnd);
            if (d >= 96) return d / 96.0;
        }
        catch { }
        return 1.0;
    }

    /// <summary>前台窗口所属进程名（不含扩展名），失败返回空串。</summary>
    public static string GetForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "";
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return "";
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";
            return Path.GetFileNameWithoutExtension(sb.ToString(0, (int)size));
        }
        finally { CloseHandle(h); }
    }

    /// <summary>前台窗口是否铺满其所在显示器（全屏）。</summary>
    public static bool IsForegroundFullScreen()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        // 桌面外壳窗口（Progman/WorkerW）矩形铺满整个显示器，但它们是壁纸宿主而非"全屏应用"。
        // 焦点落在桌面且无前台应用时若不排除，灵动岛会误让位（v3.6.2 实测）。
        var cls = new System.Text.StringBuilder(256);
        if (GetClassName(hwnd, cls, cls.Capacity) > 0)
        {
            var c = cls.ToString();
            if (c is "Progman" or "WorkerW") return false;
        }
        if (!GetWindowRect(hwnd, out var wr)) return false;
        var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (mon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        const int tol = 4;
        return Math.Abs(wr.Left - mi.rcMonitor.Left) <= tol
            && Math.Abs(wr.Top - mi.rcMonitor.Top) <= tol
            && Math.Abs(wr.Right - mi.rcMonitor.Right) <= tol
            && Math.Abs(wr.Bottom - mi.rcMonitor.Bottom) <= tol;
    }
}
