using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Classify.Services;

namespace Classify.UI;

/// <summary>
/// 类灵动岛常驻通知条：屏幕顶部悬浮胶囊、无焦点；
/// 有教师常驻通知时横排气泡展示（多教师并列）；
/// 空闲时轮换一言诗词（v1.hitokoto.cn，离线用缓存/内置兜底）。
/// 整窗即胶囊：颜色铺满 + DWM 圆角（自绘圆角必然露角——v2 U1 教训）。
/// </summary>
public class IslandWindow : Window
{
    public static IslandWindow? Current { get; private set; }

    private const double PillHeightDip = 44;
    private const string BgNotice = "#FF4655D6";
    private const string BgIdle = "#E61D2B64"; // AARRGGBB：90% 不透明深蓝

    private readonly TextBlock _pillText = new()
    {
        FontSize = 15,
        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xFF)),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Segoe UI Emoji"),
    };

    private readonly Border _pill = new() { Child = null };

    private DispatcherTimer? _quoteTimer;
    private DispatcherTimer? _noticeTimer;
    private DispatcherTimer? _relayoutTimer;
    private DispatcherTimer? _concedeTimer;
    private bool _conceded;
    private string _lastQuote = "";
    private List<(string Name, string Text)> _notices = new();

    private static readonly string[] FallbackQuotes =
    {
        "问渠那得清如许，为有源头活水来。——朱熹",
        "纸上得来终觉浅，绝知此事要躬行。——陆游",
        "黑发不知勤学早，白首方悔读书迟。——颜真卿",
        "随风潜入夜，润物细无声。——杜甫",
        "少壮不努力，老大徒伤悲。——《长歌行》",
    };

    public IslandWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Title = "Classify 灵动岛";
        Background = Brushes.Transparent;
        Content = _pill;
        _pill.Child = _pillText;
        _pill.MouseLeftButtonUp += (_, _) => App.TryShowMainWindow();
        Cursor = Cursors.Hand;
        Current = this;
        Closed += (_, _) => Cleanup();
    }

    /// <summary>启动时默认显示（IslandEnabled=true 时由 App.StartServices 调用）。</summary>
    public static void ShowDefault()
    {
        if (Current is { } w && w.IsVisible) return;
        if (Current is null) Current = new IslandWindow();
        Current.ShowInternal();
    }

    public static void Toggle()
    {
        if (Current is { } w)
        {
            w.Close();
            Current = null;
            SettingsService.Instance.Values.IslandEnabled = false;
            SettingsService.Instance.Save();
            return;
        }
        ShowDefault();
        SettingsService.Instance.Values.IslandEnabled = true;
        SettingsService.Instance.Save();
    }

    public static void Refresh()
    {
        if (Current is { } w && w.IsVisible) _ = w.ReloadAsync();
    }

    private void ShowInternal()
    {
        WindowUtil.ConfigureFloating(this);
        Layout();
        Show();
        WindowUtil.SetTopmost(this);

        StartTimers();
        _ = ReloadAsync();
        if (_notices.Count == 0) UpdateIdlePill(); // 启动即取一言，不空等 5 分钟
    }

    private void StartTimers()
    {
        if (_quoteTimer is null)
        {
            _quoteTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _quoteTimer.Tick += (_, _) => UpdateIdlePill();
            _quoteTimer.Start();
        }
        if (_noticeTimer is null)
        {
            // 通知同步：20 秒（KV 边缘传播 ≤60s，缩短感知延迟）
            _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _noticeTimer.Tick += (_, _) => _ = ReloadAsync();
            _noticeTimer.Start();
        }
        if (_relayoutTimer is null)
        {
            // 分辨率 / DPI / 任务栏变化后重新按当前工作区布局
            _relayoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _relayoutTimer.Tick += (_, _) => Layout();
            _relayoutTimer.Start();
        }
        if (_concedeTimer is null)
        {
            // 全屏让位：任何软件（含本应用的全屏时钟/看板/点名）进入全屏时，岛自动隐藏让位；
            // 对方退出全屏后 2 秒内自动回来。不与全屏应用抢置顶。
            _concedeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _concedeTimer.Tick += (_, _) => UpdateConcede();
            _concedeTimer.Start();
        }
        UpdateConcede();
    }

    private void UpdateConcede()
    {
        bool ours = ClockWindow.Current != null || ReadBoardWindow.Current != null || PickerWindow.Current != null;
        bool concede = ours || Interop.Win32.IsForegroundFullScreen();
        if (concede == _conceded) return;
        _conceded = concede;
        App.Log.Info($"灵动岛{(concede ? "让位（检测到全屏窗口）" : "回归")}");
        if (concede)
        {
            Hide();
        }
        else
        {
            Layout();
            WindowUtil.ShowWithoutActivate(this);
            _ = ReloadAsync();
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            var r = await new ApiService().GetDeviceNoticesAsync();
            _notices = r.Select(n => (n.name, n.text)).ToList();
        }
        catch (Exception ex) { App.Log.Aggregate("灵动岛通知", ex); }
        _ = Dispatcher.BeginInvoke(new Action(Layout));
    }

    /// <summary>布局：整个窗口即一颗胶囊（无留白无边），有通知 → 通知内容；无 → 一言。居中于工作区顶部。</summary>
    private void Layout()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Layout)); return; }
        try
        {
            var hwnd = WindowUtil.Hwnd(this);
            double dpi = WindowUtil.GetDpiForHwnd(hwnd);
            var wa = Interop.Win32.WorkAreaOfHwnd(hwnd);

            string text;
            string bg;
            Brush fg;
            if (_notices.Count > 0)
            {
                text = "📌 " + string.Join("　·　", _notices.Select(n => $"{n.Name}：{n.Text}"));
                bg = BgNotice;
                fg = Brushes.White;
            }
            else
            {
                text = _lastQuote.Length > 0 ? _lastQuote : "Classify";
                bg = BgIdle;
                fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xFF));
            }

            string pillBg = bg;

            _pillText.Text = text;
            _pillText.Foreground = fg;
            _pill.Background = (Brush)new BrushConverter().ConvertFromString(pillBg)!;

            // 按内容估算宽度（中文全角约 1 字号宽，英文约半宽），居中于工作区顶部
            double estimated = text.Sum(c => c > 0x2E80 ? 1.05 : 0.55) * _pillText.FontSize + 48;
            int width = (int)Math.Clamp(estimated * dpi, 320 * dpi, Math.Max(320 * dpi, (wa.Right - wa.Left - 40)));
            int height = (int)(PillHeightDip * dpi);
            // 吸附屏幕顶端：贴住工作区顶边（留 4px 呼吸缝让圆角完整可见）
            Width = width / dpi;
            Height = height / dpi;
            Left = (wa.Left + Math.Max(0, (wa.Right - wa.Left - width) / 2)) / dpi;
            Top = (wa.Top + 4 * dpi) / dpi;

            // 被其他置顶条（如音乐歌词）/显示变更挤掉时自我恢复（让位期间除外）
            if (!IsVisible && Current == this && !_conceded)
                WindowUtil.ShowWithoutActivate(this);
        }
        catch (Exception ex) { App.Log.Aggregate("灵动岛布局", ex); }
    }

    private void UpdateIdlePill()
    {
        if (_notices.Count > 0) return; // 有常驻通知时不轮换一言
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                using var resp = await http.GetAsync("https://v1.hitokoto.cn/?c=i");
                var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
                var sentence = json.GetProperty("hitokoto").GetString() ?? "";
                var from = json.TryGetProperty("from_who", out var w) && w.ValueKind == JsonValueKind.String
                    ? w.GetString() : null;
                var work = json.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String
                    ? f.GetString() : null;
                var quote = sentence + (string.IsNullOrEmpty(work) ? "" :
                    $"——{(string.IsNullOrEmpty(from) ? "" : from + " ")}《{work}》");
                _lastQuote = quote;
            }
            catch
            {
                if (_lastQuote.Length == 0)
                    _lastQuote = FallbackQuotes[Random.Shared.Next(FallbackQuotes.Length)];
            }
            _ = Dispatcher.BeginInvoke(new Action(Layout));
        });
    }

    private void Cleanup()
    {
        _quoteTimer?.Stop();
        _noticeTimer?.Stop();
        _relayoutTimer?.Stop();
        _concedeTimer?.Stop();
        if (Current == this) Current = null;
    }
}
