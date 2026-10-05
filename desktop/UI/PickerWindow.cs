using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Classify.Services;

namespace Classify.UI;

/// <summary>
/// 随机点名（全屏）：从早读看板名册随机抽一名学生——
/// 姓名快速滚动 ~1.2 秒后定格放大并 TTS 朗读"请 XXX 同学"。
/// Esc / 点击 / 12 秒自动退出。名册为空时 Toast 提示并不下发。
/// </summary>
public class PickerWindow : Window
{
    public static PickerWindow? Current { get; private set; }

    private readonly List<(string Group, string Name)> _roster;
    private readonly Image _wallpaper = new() { Stretch = Stretch.UniformToFill, Opacity = 0.35 };
    private readonly TextBlock _label = MakeText("🎲 随机点名", 20, FontWeights.Normal,
        new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xBC)));
    private readonly TextBlock _name = MakeText("", 72, FontWeights.Bold, Brushes.White);
    private readonly DispatcherTimer _spin;
    private readonly DispatcherTimer _autoClose;
    private readonly DispatcherTimer _closeDelay;
    private readonly Random _rng = new();
    private int _ticks;

    private PickerWindow(List<(string Group, string Name)> roster)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Title = "Classify 随机点名";
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x20));
        _roster = roster;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(_label);
        _name.Margin = new Thickness(0, 30, 0, 0);
        stack.Children.Add(_name);
        var hint = MakeText("点击或按 Esc 结束", 13, FontWeights.Normal,
            new SolidColorBrush(Color.FromRgb(0x4A, 0x54, 0x70)));
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.Margin = new Thickness(0, 60, 0, 0);
        stack.Children.Add(hint);
        var root = new Grid();
        root.Children.Add(_wallpaper);
        Wallpaper.Apply(_wallpaper, opacity: 0.35, blurRadius: 34);
        root.Children.Add(stack);
        Content = root;

        MouseDown += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        _spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _spin.Tick += (_, _) =>
        {
            _ticks++;
            var (g, n) = _roster[_rng.Next(_roster.Count)];
            _name.Text = n;
            _label.Text = $"🎲 {g}";
            if (_ticks >= 18)
            {
                _spin.Stop();
                AnnounceWinner();
            }
        };
        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(14) };
        _autoClose.Tick += (_, _) => Close();
        _closeDelay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _closeDelay.Tick += (_, _) => Close();
        Current = this;
    }

    private static TextBlock MakeText(string text, double size, FontWeight weight, Brush brush) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Segoe UI Emoji"),
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>入口：名册为空时提示并返回 false（不产生 displayed 回执）。</summary>
    public static bool TryStart()
    {
        if (Current != null) return true;
        var state = ReadBoardState.Load();
        var roster = state.Groups
            .SelectMany(g => g.Members.Select(m => (Group: g.Name, Name: m.Name)))
            .ToList();
        if (roster.Count == 0)
        {
            ToastWindow.ShowToast("名册为空", "请先在早读看板中添加小组成员（可联动 EasiCare 名册一键导入）");
            return false;
        }
        Current = new PickerWindow(roster);
        Current.ShowInternal();
        return true;
    }

    private void ShowInternal()
    {
        WindowUtil.EnterFullScreen(this);
        Show();
        _spin.Start();
        _autoClose.Start();
    }

    private void AnnounceWinner()
    {
        var (g, n) = _roster[_rng.Next(_roster.Count)];
        _label.Text = $"🎲 {g} · 请回答";
        _name.Text = n;
        _name.FontSize = 120;
        _name.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
        _ = TtsService.Instance.SpeakAsync($"请{n}同学来回答。");
        _closeDelay.Start(); // 朗读留足时间后再自动退出
    }

    protected override void OnClosed(EventArgs e)
    {
        _spin?.Stop();
        _autoClose?.Stop();
        _closeDelay?.Stop();
        if (Current == this) Current = null;
        base.OnClosed(e);
    }
}
