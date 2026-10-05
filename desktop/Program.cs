using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
    const string RegKey = @"HKEY_CURRENT_USER\Software\ClassifyLite";
    const string Repo = "Linkium-DevTeam/Classify";
    static readonly string[] DownloadMirrors = { "", "https://ghfast.top/", "https://gh-proxy.com/" }; // 空串=直连
    static string SERVER = "";   // 首次运行手动输入，存 HKCU 注册表；不内置任何服务端
    static string _code = "";
    static string _skippedVersion = "";  // “不更新”：本次会话内跳过该版本
    static Window _mainWin = null!;
    static TextBlock _statusText = null!;
    static OverlayWindow _overlay = null!;
    static DispatcherTimer _pollTimer = null!;
    static DispatcherTimer _collapseTimer = null!;
    static System.Windows.Forms.NotifyIcon _tray = null!;
    static SpeechSynthesizer _tts = null!;
    static bool _ttsEnabled = true;

    [STAThread]
    static void Main()
    {
        ApplyStaged(); // 闲时更新：先消费上次下载好的新版本（可能换装自身 exe）
        var app = new WpfApp();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SERVER = LoadServer();
        if (SERVER.Length == 0)
        {
            SERVER = AskServer();
            if (SERVER.Length == 0) return; // 取消 = 退出
            SaveServer(SERVER);
        }
        _code = GetDeviceCode();
        _mainWin = BuildMainWindow();
        SetupTray();

        var pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        pollTimer.Tick += async (_, _) => await PollAsync();
        pollTimer.Start();
        _ = PollAsync();
        _ = RegisterAsync();

        // 自动收起：主窗口 2 分钟后自动隐藏到托盘（展示口令用，不需要常驻桌面）
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _collapseTimer.Tick += (_, _) => CollapseMain();
        _collapseTimer.Start();
        _ = Task.Run(UpdateLoop); // OTA：启动即查一次，之后每 6 小时

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

    static string LoadServer()
    {
        try { return (Microsoft.Win32.Registry.GetValue(RegKey, "Server", "") as string ?? "").TrimEnd('/'); }
        catch { return ""; }
    }

    static void SaveServer(string url)
    {
        try { Microsoft.Win32.Registry.SetValue(RegKey, "Server", url); } catch { }
    }

    /// <summary>首次运行（或注册表被清）时手动输入服务端地址。返回空串 = 用户取消。</summary>
    static string AskServer()
    {
        var urlBox = new System.Windows.Controls.TextBox
        {
            Text = "https://",
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 15,
            Padding = new Thickness(8, 6, 8, 6),
        };
        var tip = new System.Windows.Controls.TextBlock
        {
            Text = "填入你部署的 Cloudflare Worker 地址（worker 目录 wrangler deploy 后输出）",
            FontSize = 12, Foreground = MBrushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        };
        var ok = new System.Windows.Controls.Button { Content = "确定", IsDefault = true, Width = 110, Height = 32, Margin = new Thickness(0, 16, 0, 0) };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Classify Lite · 服务端地址",
            FontSize = 18, FontWeight = FontWeights.Bold,
        });
        panel.Children.Add(urlBox);
        panel.Children.Add(tip);
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "Classify Lite",
            Width = 460, SizeToContent = System.Windows.SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Content = panel,
        };
        string result = "";
        ok.Click += (_, _) =>
        {
            var u = urlBox.Text.Trim().TrimEnd('/');
            if (u.StartsWith("http://") || u.StartsWith("https://")) { result = u; win.Close(); }
            else tip.Text = "地址必须以 http:// 或 https:// 开头";
        };
        win.Closed += (_, _) => { if (result.Length == 0) Environment.Exit(0); };
        win.ShowDialog();
        return result;
    }

    // ---------------- OTA：GitHub Releases（非静默：弹窗展示 changelog，三按钮） ----------------
    static Version CurrentVersion => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;

    sealed record ReleaseInfo(string Version, string Changelog, string AssetUrl, long Size);

    static async Task UpdateLoop()
    {
        ApplyStaged(); // 闲时更新的安装点：上次“闲时更新”下载好的新版本，本次启动静默换上
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (true)
        {
            try { await CheckUpdateAsync(false); } catch { }
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    static async Task<ReleaseInfo?> FetchLatestAsync()
    {
        using var http = await GitHubHttpAsync();
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var ver = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (ver.Length == 0) return null;
        string assetUrl = ""; long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var nm = a.GetProperty("name").GetString() ?? "";
            if (nm.StartsWith("Clite-") && nm.EndsWith(".zip"))
            {
                assetUrl = a.GetProperty("browser_download_url").GetString() ?? "";
                size = a.GetProperty("size").GetInt64(); break;
            }
        }
        if (assetUrl.Length == 0) return null;
        var body = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
        return new ReleaseInfo(ver, body, assetUrl, size);
    }

    /// <summary>GitHub API 客户端：国内环境先用阿里 DoH 解析 api.github.com 真实 IP 直连（SNI 不变）。</summary>
    static async Task<HttpClient> GitHubHttpAsync()
    {
        var handler = new SocketsHttpHandler
        {
            // 国内环境：连接前先用阿里 DoH 解析真实 IP，SNI/Host 保持原域名不变
            ConnectCallback = async (ctx, ct) =>
            {
                var ip = await DohResolve(ctx.DnsEndPoint.Host);
                if (ip != null)
                {
                    var s1 = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    await s1.ConnectAsync(new System.Net.IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                    return new NetworkStream(s1, ownsSocket: true);
                }
                var s2 = new Socket(ctx.DnsEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await s2.ConnectAsync(ctx.DnsEndPoint, ct);
                return new NetworkStream(s2, ownsSocket: true);
            },
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Classify-Lite-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    static async Task<IPAddress?> DohResolve(string host)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var json = await http.GetStringAsync($"https://dns.alidns.com/resolve?name={host}&type=A");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var ans in doc.RootElement.GetProperty("Answer").EnumerateArray())
                if (ans.TryGetProperty("type", out var t) && t.GetInt32() == 1)
                    return IPAddress.Parse(ans.GetProperty("data").GetString()!);
        }
        catch { }
        return null;
    }

    static async Task CheckUpdateAsync(bool manual)
    {
        ReleaseInfo? info = null;
        try { info = await FetchLatestAsync(); } catch { }
        if (info is null)
        {
            if (manual) Toast("检查更新失败", "无法连接 GitHub（可稍后重试）");
            return;
        }
        if (!manual && info.Version == _skippedVersion) return;
        if (Version.TryParse(info.Version, out var nv) && nv <= CurrentVersion)
        {
            if (manual) Toast("检查更新", "已是最新版本 " + CurrentVersion.ToString(3));
            return;
        }
        ShowUpdateDialog(info, manual);
    }

    /// <summary>非静默更新对话框：changelog + 不更新 / 闲时更新 / 立即更新。</summary>
    static void ShowUpdateDialog(ReleaseInfo info, bool manual)
    {
        _mainWin.Dispatcher.Invoke(() =>
        {
            var changelog = new System.Windows.Controls.TextBox
            {
                Text = string.IsNullOrWhiteSpace(info.Changelog) ? "（无更新说明）" : info.Changelog,
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Height = 200, FontSize = 12.5,
                Background = MBrushes.White, Foreground = MBrushes.Black,
            };
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            System.Windows.Controls.Button B(string content, System.Windows.Input.Key? k = null)
            {
                var b = new System.Windows.Controls.Button { Content = content, Width = 108, Height = 34, Margin = new Thickness(0, 0, 10, 0) };
                if (k.HasValue) b.IsDefault = k == System.Windows.Input.Key.Enter;
                return b;
            }
            var bSkip = B("不更新"); var bIdle = B("闲时更新"); var bNow = B("立即更新");
            row.Children.Add(bSkip); row.Children.Add(bIdle); row.Children.Add(bNow);
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"发现新版本 v{info.Version}（当前 v{CurrentVersion.ToString(3)}）",
                FontSize = 18, FontWeight = FontWeights.Bold,
            });
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "更新说明：", Margin = new Thickness(0, 12, 0, 6), FontSize = 12.5, Foreground = MBrushes.Gray,
            });
            panel.Children.Add(changelog);
            panel.Children.Add(row);
            var win = new Window
            {
                Title = "Classify Lite · 软件更新",
                Width = 520, SizeToContent = System.Windows.SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize, Content = panel,
            };
            bSkip.Click += (_, _) => { _skippedVersion = info.Version; win.Close(); };
            bIdle.Click += (_, _) => { win.Close(); _ = Task.Run(() => DownloadAndApply(info, idle: true)); };
            bNow.Click += (_, _) => { win.Close(); _ = Task.Run(() => DownloadAndApply(info, idle: false)); };
            win.Show();
        });
    }

    static void Toast(string title, string text) => _tray.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.Info);

    static string StagedPath()
    {
        var dir = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "update");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "Clite.new");
    }

    /// <summary>按 镜像→直连 顺序下载新版本到暂存位。idle=true 时只落盘等下次启动换装。</summary>
    static async Task DownloadAndApply(ReleaseInfo info, bool idle)
    {
        var staged = StagedPath();
        bool ok = false;
        foreach (var mirror in DownloadMirrors)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Classify-Lite-Updater");
                using var resp = await http.GetAsync(mirror + info.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) continue;
                await using var src = resp.Content.ReadAsStream();
                await using var dst = File.Create(staged);
                await src.CopyToAsync(dst);
                // 轻校验：zip 魔数
                await dst.FlushAsync();
                using (var fs = File.OpenRead(staged))
                {
                    if (fs.ReadByte() != 'P' || fs.ReadByte() != 'K') throw new InvalidDataException("非 zip 内容");
                }
                ok = true; break;
            }
            catch { }
        }
        if (!ok) { Toast("更新失败", "下载失败，请稍后重试或前往 GitHub Releases 手动下载"); return; }
        if (idle)
        {
            Toast("更新已就绪", "v" + info.Version + " 将在下次启动 Classify Lite 时生效");
            return; // 暂存就位，UpdateLoop 启动时的 ApplyStaged() 完成换装
        }
        ApplyStaged();
        var exe = Environment.ProcessPath!;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        Toast("更新完成", "v" + info.Version);
        Environment.Exit(0);
    }

    /// <summary>启动时消费暂存的新版本：改名自身腾路径 → 换上 → 顺手清 .old。</summary>
    static void ApplyStaged()
    {
        try
        {
            var staged = StagedPath();
            if (!File.Exists(staged)) return;
            var self = Environment.ProcessPath!;
            var old = self + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(self, old);
            File.Move(staged, self);
            File.Delete(old);
            Toast("更新完成", "Classify Lite 已换装为新版本");
        }
        catch { try { File.Delete(StagedPath()); } catch { } }
    }

    static string GetDeviceCode()
    {
        try
        {
            var mguid = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", "") as string ?? "";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(mguid + "|clite|v1"));
            return (BitConverter.ToUInt64(hash, 0) % 100000000).ToString("D8");
        }
        catch { return Random.Shared.Next(10000000, 99999999).ToString(); }
    }

    static async Task RegisterAsync()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new { code = _code, name = Environment.MachineName, hwid = _code, ver = "1.0.0" });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await new System.Net.Http.HttpClient().PostAsync(SERVER + "/api/register", content);
        }
        catch { }
    }

    static async Task PollAsync()
    {
        try
        {
            // 手动按 UTF-8 解码：不信任响应头 charset（部分代理会丢头导致 GetStringAsync 误用 Latin-1）
            var bytes = await new System.Net.Http.HttpClient().GetByteArrayAsync($"{SERVER}/api/poll?code={_code}");
            var resp = Encoding.UTF8.GetString(bytes);
            var doc = System.Text.Json.JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("messages", out var arr) && arr.GetArrayLength() > 0)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    var text = m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (text.Length > 0) ShowOverlay(text);
                }
            }
            SetStatus("● 已连接", MColor.FromRgb(0x3D, 0xD6, 0x8C));
        }
        catch { SetStatus("● 连接失败", MColor.FromRgb(0xFF, 0x6B, 0x6B)); }
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
            Width = 420, Height = 340,
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
        panel.Children.Add(new TextBlock { Text = "\n在手机上打开 clite.linkium.top\n输入上方 8 位口令即可发送喊话\n\n收到喊话：全屏展示 + 本地朗读\n本窗口 2 分钟后自动收起到托盘", FontSize = 12, Foreground = new SolidColorBrush(MColor.FromRgb(0x6B, 0x76, 0x94)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0) });

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
        menu.Items.Add("检查更新", default, (_, _) => { Toast("检查更新", "正在连接 GitHub…"); _ = Task.Run(() => CheckUpdateAsync(true)); });
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
