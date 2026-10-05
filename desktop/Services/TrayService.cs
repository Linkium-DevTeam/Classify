using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Classify.UI;

namespace Classify.Services;

/// <summary>托盘图标：Win32 Shell_NotifyIcon 实现，含右键菜单与气泡通知、全局热键。</summary>
public sealed class TrayService : INotifyPropertyChanged
{
    public static TrayService Instance { get; } = new();

    private const uint WM_TRAYICON = 0x8001; // WM_APP+1
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;

    private const int ID_OPEN = 1001;
    private const int ID_TEST = 1002;
    private const int ID_EXIT = 1003;
    private const int ID_COPYURL = 1004;
    private const int ID_SETTINGS = 1005;
    private const int ID_ISLAND = 1006;
    private const int ID_BOARD = 1007;
    private const int ID_PRECLASS_NOW = 1008;
    private const int ID_PRECLASS_NEXT = 1009;
    private const int ID_DND = 1010;
    private const int ID_EASICARE = 1011;
    private const int ID_AUTOSILENT = 1012;

    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const int HOTKEY_ID_SHOW = 1;      // Ctrl+Alt+C

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD = 0x0100;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASS wc);

    [DllImport("user32.dll")]
    private static extern bool GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern long TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[]? bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    /// <summary>用 WPF 解码 PNG 并缩放，经 DIB + CreateIconIndirect 生成带透明通道的 HICON。</summary>
    private static IntPtr CreateIconFromPng(string path, int size)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            if (bmp.PixelWidth == 0 || bmp.PixelHeight == 0) return IntPtr.Zero;

            var scale = Math.Min((double)size / bmp.PixelWidth, (double)size / bmp.PixelHeight);
            var transformed = new System.Windows.Media.Imaging.TransformedBitmap(
                bmp, new System.Windows.Media.ScaleTransform(scale, scale));
            var wb = new System.Windows.Media.Imaging.WriteableBitmap(transformed);
            wb.Freeze();

            int w = wb.PixelWidth, h = wb.PixelHeight;
            var pixels = new byte[w * h * 4];
            wb.CopyPixels(pixels, w * 4, 0);

            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // 自上而下
                biPlanes = 1,
                biBitCount = 32,
            };
            var hColor = CreateDIBSection(IntPtr.Zero, ref header, 0 /*DIB_RGB_COLORS*/, out var bits, IntPtr.Zero, 0);
            if (hColor == IntPtr.Zero) return IntPtr.Zero;
            Marshal.Copy(pixels, 0, bits, pixels.Length);

            // 全零掩码 = 使用 32bpp 的 alpha 通道
            var maskBytes = new byte[(((w + 15) / 16) * 2) * h];
            var hMask = CreateBitmap(w, h, 1, 1, maskBytes);

            var info = new ICONINFO { fIcon = true, xHotspot = 0, yHotspot = 0, hbmMask = hMask, hbmColor = hColor };
            var hIcon = CreateIconIndirect(ref info);
            DeleteObject(hColor);
            DeleteObject(hMask);
            return hIcon;
        }
        catch (Exception ex)
        {
            App.Log.Error("PNG 转 HICON 失败", ex);
            return IntPtr.Zero;
        }
    }

    private delegate IntPtr WndProcHandler(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    private static WndProcHandler? _wndProcKeepAlive;
    private static IntPtr _wndProcPtr;

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _statusSuffix = "";
    public string ToolTip => string.IsNullOrEmpty(_statusSuffix) ? "Classify" : $"Classify — {_statusSuffix}";

    public void SetStatusSuffix(string suffix)
    {
        _statusSuffix = suffix;
        if (_added) ModifyTip();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTip)));
    }

    public void Init()
    {
        if (_hwnd != IntPtr.Zero) return;
        try
        {
            // 托盘图标不再走 LoadImageW 读 ico 文件（本机 Documents 目录的进程视图分裂/
            // 杀软锁会让它随机失败）——直接从 logo PNG 由 WPF 解码后 CreateIconIndirect 造 HICON，
            // 保留透明通道；失败再回退 app.ico / 系统图标。
            _icon = CreateIconFromPng(
                AppContext.BaseDirectory + "Assets\\logo256.png", 32);
            if (_icon == IntPtr.Zero)
            {
                Thread.Sleep(1200);
                _icon = CreateIconFromPng(
                    AppContext.BaseDirectory + "Assets\\logo256.png", 32);
            }
            if (_icon == IntPtr.Zero)
            {
                App.Log.Error("logo PNG 造图标失败，回退 app.ico");
                _icon = LoadImageW(IntPtr.Zero, AppContext.BaseDirectory + "Assets\\app.ico",
                    1, 16, 16, 0x10);
            }
            if (_icon == IntPtr.Zero)
                _icon = LoadImageW(IntPtr.Zero, "#32512", 1, 16, 16, 0); // 系统信息图标兜底

            _wndProcKeepAlive = WndProc;
            _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive);
            var cls = new WNDCLASS
            {
                lpfnWndProc = _wndProcPtr,
                hInstance = GetModuleHandleW(null),
                lpszClassName = "Classify_TrayWnd",
            };
            RegisterClassW(ref cls);
            _hwnd = CreateWindowExW(0x80 /*WS_EX_TOOLWINDOW*/, cls.lpszClassName, "Classify",
                0, 0, 0, 0, 0, new IntPtr(-3) /*HWND_MESSAGE*/, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { App.Log.Error("托盘窗口创建失败"); return; }

            var nid = NewData();
            nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            _added = Shell_NotifyIconW(NIM_ADD, ref nid);
            App.Log.Info($"托盘图标 {(_added ? "创建成功" : "创建失败")}");

            // 全局热键：Ctrl+Alt+C 唤起主界面；Ctrl+Alt+V 推送本机剪贴板
            RegisterHotKey(_hwnd, HOTKEY_ID_SHOW, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x43);
            // Ctrl+Alt+V 剪贴板推送已随剪贴板同步功能下线

            var thread = new Thread(() =>
            {
                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            })
            { IsBackground = true };
            thread.Start();
        }
        catch (Exception ex)
        {
            App.Log.Error("托盘初始化失败", ex);
        }
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _icon,
        szTip = ToolTip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void ModifyTip()
    {
        var nid = NewData();
        nid.uFlags = NIF_TIP;
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        try
        {
            var nid = NewData();
            nid.uFlags = NIF_INFO;
            nid.szInfoTitle = title;
            nid.szInfo = text;
            Shell_NotifyIconW(NIM_MODIFY, ref nid);
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        if (msg == WM_TRAYICON && lp != IntPtr.Zero)
        {
            uint ev = (uint)lp.ToInt64();
            if (ev == WM_LBUTTONDBLCLK) App.TryShowMainWindow();
            else if (ev == WM_RBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == WM_HOTKEY)
        {
            int id = (int)wp.ToInt64();
            if (id == HOTKEY_ID_SHOW) App.TryShowMainWindow();
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0 /*MF_STRING*/, (IntPtr)ID_OPEN, "打开主界面");
        AppendMenuW(menu, 0, (IntPtr)ID_TEST, "测试喊话");
        AppendMenuW(menu, 0, (IntPtr)ID_PRECLASS_NOW, "⚡ 候课（当节学科）");
        AppendMenuW(menu, 0, (IntPtr)ID_PRECLASS_NEXT, "⚡ 候课（下一节学科）");
        AppendMenuW(menu, 0, (IntPtr)ID_ISLAND,
            IslandWindow.Current != null ? "灵动岛（已开启）" : "灵动岛（已关闭）");
        AppendMenuW(menu, 0, (IntPtr)ID_BOARD,
            ReadBoardWindow.Current != null ? "早读看板（已开启）" : "早读看板（已关闭）");
        AppendMenuW(menu, 0, (IntPtr)ID_DND, AnnouncementService.DndActive()
            ? "🌙 勿扰模式（点击关闭）" : "🌙 勿扰 30 分钟（喊话静音）");
        AppendMenuW(menu, 0, (IntPtr)ID_EASICARE,
            SettingsService.Instance.Values.EasiCareEnabled
                ? "EasiCare 评价联动（已开启）" : "EasiCare 评价联动（已关闭）");
        AppendMenuW(menu, 0, (IntPtr)ID_AUTOSILENT,
            SettingsService.Instance.Values.AutoUpdateSilent
                ? "自动更新（静默，点击改为询问）" : "自动更新（询问，点击改为静默）");
        AppendMenuW(menu, 0, (IntPtr)ID_COPYURL, "复制手机端地址");
        AppendMenuW(menu, 0, (IntPtr)ID_SETTINGS, "设置");
        AppendMenuW(menu, 0x800 /*MF_SEPARATOR*/, IntPtr.Zero, null);
        AppendMenuW(menu, 0, (IntPtr)ID_EXIT, "退出");
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // TrackPopupMenu 需要前台窗口
        long cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RETURNCMD,
            pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        switch (cmd)
        {
            case ID_OPEN: App.TryShowMainWindow(); break;
            case ID_COPYURL:
                var url = SettingsService.Instance.Values.ServerUrl;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    try { Clipboard.SetText(url); } catch { }
                }
                break;
            case ID_SETTINGS: App.ShowMainWindowTab("settings"); break;
            case ID_ISLAND: IslandWindow.Toggle(); break;
            case ID_PRECLASS_NOW: _ = SmartPreclassService.RunNowAsync(); break;
            case ID_PRECLASS_NEXT: _ = SmartPreclassService.RunNextNowAsync(); break;
            case ID_EASICARE:
                {
                    var on = !SettingsService.Instance.Values.EasiCareEnabled;
                    if (on && !UI.AboutPage.EasiCareHelperClosedSourceWarning()) return; // 取消则不变更
                    SettingsService.Instance.Values.EasiCareEnabled = on;
                    SettingsService.Instance.Save();
                    break;
                }
            case ID_AUTOSILENT:
                SettingsService.Instance.Values.AutoUpdateSilent = !SettingsService.Instance.Values.AutoUpdateSilent;
                SettingsService.Instance.Save();
                ToastWindow.ShowToast("自动更新", SettingsService.Instance.Values.AutoUpdateSilent
                    ? "已切换为静默模式（发现新版本自动换装）"
                    : "已切换为询问模式（发现新版本弹窗确认）");
                break;
            case ID_DND:
                if (AnnouncementService.DndActive()) AnnouncementService.SetDnd(0);
                else AnnouncementService.SetDnd(30);
                break;
            case ID_BOARD:
                if (ReadBoardWindow.Current != null) ReadBoardWindow.CloseBoard();
                else ReadBoardWindow.OpenBoard();
                break;
            case ID_TEST:
                AnnouncementService.Instance.Enqueue(new Models.AnnItem
                {
                    Text = "这是一条测试喊话 ✅ Classify 工作正常。",
                    Speak = true,
                    From = "本机",
                });
                break;
            case ID_EXIT:
                try
                {
                    var nid = NewData();
                    Shell_NotifyIconW(NIM_DELETE, ref nid);
                }
                catch { }
                Companion.SignalExit(); // 主动退出：通知伴生不要拉起
                MetricsService.MarkCleanExit();
                SettingsService.Instance.Save();
                Environment.Exit(0);
                break;
        }
    }
}
