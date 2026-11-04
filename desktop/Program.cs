using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.IO;
using System.Linq;
using System.Text;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WpfApp = System.Windows.Application;
using WpfFontFamily = System.Windows.Media.FontFamily;
using HAlign = System.Windows.HorizontalAlignment;
using MColor = System.Windows.Media.Color;
using MBrushes = System.Windows.Media.Brushes;

[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

namespace Clite;

public static class Program
{
    static readonly string Server = ResolveServer();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static readonly string Version =
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";

    static string _code = "";
    static Window _mainWin = null!;
    static TextBlock _statusText = null!;
    static OverlayWindow _overlay = null!;
    static DispatcherTimer _pollTimer = null!;
    static DispatcherTimer _collapseTimer = null!;
    static System.Windows.Forms.NotifyIcon _tray = null!;
    static SpeechSynthesizer _tts = null!;
    static bool _ttsEnabled = true;
    static bool _polling;
    static bool _registered;
    static DateTime _lastRegister = DateTime.MinValue;

    [STAThread]
    static void Main()
    {
        var app = new WpfApp();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _code = GetDeviceCode();
        _mainWin = BuildMainWindow();
        SetupTray();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _pollTimer.Start();
        _ = PollAsync();
        _ = RegisterAsync();

        // 自动收起：主窗口 2 分钟后自动隐藏到托盘（展示口令用，不需要常驻桌面）
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _collapseTimer.Tick += (_, _) => CollapseMain();
        _collapseTimer.Start();

        _mainWin.Icon = LoadLogo(); // 窗口标题栏 + 任务栏
        _mainWin.Show(); // 老师启动后必须立刻能看到设备口令

        app.Run();
    }

    static ImageSource LoadLogo()
    {
        try
        {
            using var s = typeof(Program).Assembly.GetManifestResourceStream("clite_logo.png");
            if (s == null) return null;
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.StreamSource = s;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            using var s = typeof(Program).Assembly.GetManifestResourceStream("clite.ico");
            if (s != null) return new System.Drawing.Icon(s, 32, 32);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    static void CollapseMain()
    {
        try { _collapseTimer.Stop(); _mainWin.Hide(); } catch { }
    }

    static void ShowMain()
    {
        try
        {
            _mainWin.Show();
            _mainWin.WindowState = WindowState.Normal;
            _collapseTimer.Stop();
            _collapseTimer.Start(); // 重新计时 2 分钟
        }
        catch { }
    }

    static void OpenControlPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Server)
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    static string ResolveServer()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("CLITE_SERVER");
            if (IsHttpUrl(env)) return env!.Trim().TrimEnd('/');
            var cfg = Path.Combine(AppContext.BaseDirectory, "clite-server.txt");
            if (File.Exists(cfg) && IsHttpUrl(File.ReadAllText(cfg))) return File.ReadAllText(cfg).Trim().TrimEnd('/');
        }
        catch { }
        return "https://clite.linkium.top";

        static bool IsHttpUrl(string value) =>
            !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
    }

    static string GetDeviceCode()
    {
        try
        {
            var mguid = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", "") as string ?? "";
            // MachineGuid 不可用时退回机器名+用户名，保证同一台教室机口令稳定
            var seed = string.IsNullOrWhiteSpace(mguid)
                ? Environment.MachineName + "|" + Environment.UserName + "|clite|v1"
                : mguid + "|clite|v1";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return (BitConverter.ToUInt64(hash, 0) % 100000000).ToString("D8");
        }
        catch { return Random.Shared.Next(10000000, 99999999).ToString("D8"); }
    }

    static async Task RegisterAsync()
    {
        if (_registered) return;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                code = _code,
                name = Environment.MachineName,
                hwid = _code,
                ver = Version,
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(Server + "/api/register", content);
            _registered = resp.IsSuccessStatusCode;
            _lastRegister = DateTime.Now;
        }
        catch { _lastRegister = DateTime.Now; }
    }

    static async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            // 手动按 UTF-8 解码：不信任响应头 charset（部分代理会丢头导致 GetStringAsync 误用 Latin-1）
            using var resp = await Http.GetAsync(Server + "/api/poll?code=" + _code);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException("HTTP " + (int)resp.StatusCode);
            var text = Encoding.UTF8.GetString(bytes);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("messages", out var arr) && arr.GetArrayLength() > 0)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    var content = m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (content.Length > 0) ShowOverlay(content);
                }
            }
            SetStatus("● 已连接", MColor.FromRgb(0x3D, 0xD6, 0x8C));
            if (!_registered && (DateTime.Now - _lastRegister).TotalSeconds > 60)
                await RegisterAsync();
        }
        catch
        {
            SetStatus("● 连接失败", MColor.FromRgb(0xFF, 0x6B, 0x6B));
            // 离线启动/服务端重置后自动补注册（最多每分钟一次）
            if (!_registered && (DateTime.Now - _lastRegister).TotalSeconds > 60)
                await RegisterAsync();
        }
        finally { _polling = false; }
    }

    static void SetStatus(string text, MColor color)
    {
        try
        {
            _mainWin.Dispatcher.Invoke(() =>
            {
                if (_statusText != null) { _statusText.Text = text; _statusText.Foreground = new SolidColorBrush(color); }
            });
        }
        catch { }
    }

    static void ShowOverlay(string text)
    {
        try
        {
            _mainWin.Dispatcher.Invoke(() =>
            {
                _overlay?.Close();
                _overlay = new OverlayWindow(text);
                _overlay.Show();
            });
            Speak(text);
        }
        catch { }
    }

    static void Speak(string text)
    {
        if (!_ttsEnabled) return;
        try
        {
            if (_tts == null)
            {
                _tts = new SpeechSynthesizer();
                _tts.Volume = 100;
                var zh = _tts.GetInstalledVoices()
                    .FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "zh");
                if (zh != null) _tts.SelectVoice(zh.VoiceInfo.Name); // 没有中文语音则用系统默认
            }
            _tts.SpeakAsyncCancelAll(); // 连续来消息时不排队叠音
            _tts.SpeakAsync(text);
        }
        catch { }
    }

    static Window BuildMainWindow()
    {
        var w = new Window
        {
            Title = "Classify Lite",
            Width = 420, Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(MColor.FromRgb(0x0B, 0x10, 0x20)),
        };
        w.Closing += (_, e) => { e.Cancel = true; w.Hide(); };

        var panel = new StackPanel { Margin = new Thickness(30, 20, 30, 20) };
        panel.Children.Add(new TextBlock { Text = "Classify Lite", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = MBrushes.White, HorizontalAlignment = HAlign.Center, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock { Text = "设备口令", FontSize = 12, Foreground = new SolidColorBrush(MColor.FromRgb(0x9A, 0xA3, 0xBC)), HorizontalAlignment = HAlign.Center, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = _code, FontSize = 36, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(MColor.FromRgb(0x7C, 0x8C, 0xFF)), HorizontalAlignment = HAlign.Center, Margin = new Thickness(0, 0, 0, 16) });

        _statusText = new TextBlock { Text = "连接中…", FontSize = 12, Foreground = new SolidColorBrush(MColor.FromRgb(0x9A, 0xA3, 0xBC)), HorizontalAlignment = HAlign.Center };
        panel.Children.Add(_statusText);
        var host = Server.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        panel.Children.Add(new TextBlock
        {
            Text = "\n在手机上打开 " + host + "\n输入上方 8 位口令即可发送喊话\n\n收到喊话：全屏展示 + 本地朗读\n本窗口 2 分钟后自动收起到托盘",
            FontSize = 12,
            Foreground = new SolidColorBrush(MColor.FromRgb(0x6B, 0x76, 0x94)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 20, 0, 0),
        });

        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HAlign.Center, Margin = new Thickness(0, 14, 0, 0) };
        var openBtn = new System.Windows.Controls.Button { Content = "打开控制页", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        openBtn.Click += (_, _) => OpenControlPage();
        var copyBtn = new System.Windows.Controls.Button { Content = "复制口令", Padding = new Thickness(12, 4, 12, 4) };
        copyBtn.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(_code); SetStatus("● 口令已复制", MColor.FromRgb(0x3D, 0xD6, 0x8C)); } catch { }
        };
        actions.Children.Add(openBtn);
        actions.Children.Add(copyBtn);
        panel.Children.Add(actions);
        panel.Children.Add(new TextBlock
        {
            Text = "Classify Lite v" + Version + " · 免安装单文件",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(MColor.FromRgb(0x6B, 0x76, 0x94)),
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 10, 0, 0),
        });

        w.Content = panel;
        return w;
    }

    static void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Classify Lite",
            Visible = true,
            Icon = LoadTrayIcon(),
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示", default, (_, _) => ShowMain());
        menu.Items.Add("复制设备口令", default, (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(_code); } catch { }
        });
        menu.Items.Add("打开控制页", default, (_, _) => OpenControlPage());
        var ttsItem = new System.Windows.Forms.ToolStripMenuItem("本地朗读") { Checked = _ttsEnabled };
        ttsItem.Click += (_, _) => { _ttsEnabled = !_ttsEnabled; ttsItem.Checked = _ttsEnabled; };
        menu.Items.Add(ttsItem);
        var autoStartItem = new System.Windows.Forms.ToolStripMenuItem("开机自启") { Checked = IsAutoStart() };
        autoStartItem.Click += (_, _) =>
        {
            try
            {
                using var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
                if (IsAutoStart()) { rk.DeleteValue("ClassifyLite", false); autoStartItem.Checked = false; }
                else { rk.SetValue("ClassifyLite", "\"" + Environment.ProcessPath + "\""); autoStartItem.Checked = true; }
            }
            catch { }
        };
        menu.Items.Add(autoStartItem);
        menu.Items.Add("退出", default, (_, _) =>
        {
            _tray.Visible = false;
            _pollTimer?.Stop();
            Environment.Exit(0);
        });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    static bool IsAutoStart()
    {
        try
        {
            var v = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "ClassifyLite", null) as string;
            return !string.IsNullOrEmpty(v);
        }
        catch { return false; }
    }

    public class OverlayWindow : Window
    {
        public OverlayWindow(string text)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true; // 课堂喊话必须盖住一切（含全屏应用、抢焦点保护的场景）
            Background = new SolidColorBrush(MColor.FromRgb(0x0B, 0x10, 0x20));
            WindowState = WindowState.Maximized;

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HAlign.Center };
            stack.Children.Add(new TextBlock
            {
                Text = "📢",
                FontSize = 48,
                Foreground = new SolidColorBrush(MColor.FromRgb(0xFF, 0xD5, 0x4F)), // WPF 无彩色 emoji，单色字形用前景色
                HorizontalAlignment = HAlign.Center,
                Margin = new Thickness(0, 0, 0, 30),
            });
            var txt = new TextBlock
            {
                Text = text,
                FontSize = text.Length > 100 ? 32 : text.Length > 50 ? 42 : 56,
                FontWeight = FontWeights.Bold,
                Foreground = MBrushes.White,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 1100,
                HorizontalAlignment = HAlign.Center,
                FontFamily = new WpfFontFamily("Segoe UI, Microsoft YaHei UI"),
            };
            stack.Children.Add(txt);
            stack.Children.Add(new TextBlock { Text = "\n点击任意位置关闭", FontSize = 14, Foreground = new SolidColorBrush(MColor.FromRgb(0x6B, 0x76, 0x94)), HorizontalAlignment = HAlign.Center, Margin = new Thickness(0, 40, 0, 0) });

            // 背景：桌面壁纸 + 模糊 + 深色蒙版（与完整版统一）；取不到壁纸退回纯深色
            var root = new Grid();
            var wp = LoadWallpaper();
            if (wp != null)
            {
                root.Children.Add(new System.Windows.Controls.Image
                {
                    Source = wp,
                    Stretch = Stretch.UniformToFill,
                    Effect = new BlurEffect { Radius = 40, KernelType = KernelType.Gaussian },
                });
                root.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Fill = new SolidColorBrush(MColor.FromArgb(0xE0, 0x0B, 0x10, 0x20)), // 88% 深色蒙版，保文字对比度
                });
            }
            root.Children.Add(stack);
            Content = root;

            MouseDown += (_, _) => Close();
            KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            // 自动收起：按阅读时长估算（10s + 每 3 字 1s），12–40 秒
            var secs = Math.Clamp(10 + text.Length / 3, 12, 40);
            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
            closeTimer.Tick += (_, _) => Close();
            closeTimer.Start();
            Closed += (_, _) => closeTimer.Stop();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(int uiAction, int uiParam, StringBuilder pvParam, int fWinIni);
    private const int SPI_GETDESKWALLPAPER = 0x0073;

    static ImageSource LoadWallpaper()
    {
        try
        {
            var sb = new StringBuilder(520);
            if (!SystemParametersInfo(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0)) return null;
            var path = sb.ToString();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }
}
