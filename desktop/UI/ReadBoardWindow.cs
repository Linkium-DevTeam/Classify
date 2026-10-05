using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Classify.Services;

namespace Classify.UI;

/// <summary>
/// 早读智能看板：全屏表面。
/// 左列：早读任务 / 荣誉榜 / 计时器；右侧：小组卡片（成员+斜杠+小红花派生）。
/// 教师分组本地保存；斜杠/红花评价联动 EasiCareHelper；状态快照上报供 WebUI 远程控制。
/// </summary>
public class ReadBoardWindow : Window
{
    public static ReadBoardWindow? Current { get; private set; }

    private readonly ReadBoardState _state;
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _tick;
    private DateTime? _timerEnd;
    private bool _flowerMode;
    /// <summary>最近一次操作看板的教师（远程来自 surface 消息 from；用于问候行按人显示）。</summary>
    private string? _greetUser;

    private readonly Grid _root = new();
    private readonly Image _wallpaper = new() { Stretch = Stretch.UniformToFill, Opacity = 0.25 };
    private readonly TextBlock _greetingText = MakeText("", 26, FontWeights.Bold, Brushes.White);
    private readonly TextBlock _subjectPill = MakeText("今日早读 · 自由早读", 14, FontWeights.SemiBold,
        new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xFF)));
    private readonly Border _teacherAvatar = new()
    {
        Width = 44, Height = 44, CornerRadius = new CornerRadius(22),
        Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _taskText = MakeText("暂无任务", 17, FontWeights.SemiBold, Brushes.White);
    private readonly TextBlock _taskHint = MakeText("", 12.5, FontWeights.Normal,
        new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xBC)));
    private readonly TextBlock _timerText = MakeText("00:00:00", 44, FontWeights.Light, Brushes.White);
    private readonly TextBlock _timerTaskText = MakeText("未开始", 12.5, FontWeights.Normal,
        new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xBC)));
    private readonly Button _timerBtn;
    private readonly SpacedPanel _honorList = new() { Gap = 4 };
    private readonly SpacedPanel _taskList = new() { Gap = 4 };
    private readonly TextBlock _honorMore = MakeText("", 12, FontWeights.Normal,
        new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xBC)));
    private readonly SpacedPanel _groupsPanel = new() { Gap = 12 };
    private readonly Border _toastHost = new()
    {
        VerticalAlignment = VerticalAlignment.Bottom,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 36),
        CornerRadius = new CornerRadius(12),
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x11, 0x16, 0x26)),
        Padding = new Thickness(18, 10, 18, 10),
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _toastText = MakeText("", 14, FontWeights.SemiBold, Brushes.White);
    private readonly TextBlock _easiCareHint = MakeText("", 12, FontWeights.Normal,
        new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xBC)));
    private readonly DispatcherTimer _topTimer;
    private readonly DispatcherTimer _easiCareRetry;

    public ReadBoardWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Title = "Classify 早读看板";
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x20));

        _state = ReadBoardState.Load();
        _timerBtn = MakeBtn("暂停", TimerBtn_Click);

        // 置顶看门狗：课件/视频等全屏窗口会把看板压下去，每 2 秒重新声明 TOPMOST
        _topTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topTimer.Tick += (_, _) => { if (IsVisible) WindowUtil.EnsureTopmost(this); };

        // EasiCare 评价缓存补写：60 秒重试一次（有缓存才真正调 API）
        _easiCareRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _easiCareRetry.Tick += async (_, _) =>
        {
            if (EasiCareClient.PendingCount > 0)
            {
                var flushed = await EasiCareClient.TryFlushPendingAsync();
                if (flushed > 0)
                {
                    ShowBoardToast($"✅ {flushed} 条缓存评价已补写入 EasiCare");
                    RenderAll();
                    _ = _state.PublishAsync();
                }
            }
        };
        _easiCareRetry.Start();

        BuildLayout();
        Content = _root;
        _ = LoadWallpaperAsync();
        _ = InitMembersAsync();
        UpdateTimerText();
        Current = this;
    }

    private static TextBlock MakeText(string text, double size, FontWeight weight, Brush brush) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Segoe UI Emoji"),
        TextWrapping = TextWrapping.Wrap,
    };

    private static Button MakeBtn(string content, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["BtnTonal"],
            FontSize = 13,
        };
        b.Click += onClick;
        return b;
    }

    private void BuildLayout()
    {
        _root.Children.Add(_wallpaper);
        _wallpaper.Effect = new BlurEffect { Radius = 28 };

        // 问候行
        var greetPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 26, 0, 0),
        };
        greetPanel.Children.Add(_teacherAvatar);
        var greetStack = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        greetStack.Children.Add(_greetingText);
        greetStack.Children.Add(_subjectPill);
        greetPanel.Children.Add(greetStack);

        // 主区：左列 320px + 右侧小组
        var main = new Grid
        {
            Margin = new Thickness(48, 22, 48, 20),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左列
        var left = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        var taskCard = Card();
        taskCard.Child = TaskCardContent();
        left.Children.Add(taskCard);

        var timerCard = Card();
        var timerStack = new SpacedPanel { Gap = 8 };
        timerStack.Children.Add(_timerText);
        timerStack.Children.Add(_timerTaskText);
        var timerBtns = new SpacedPanel { Orientation = Orientation.Horizontal, Gap = 8 };
        timerBtns.Children.Add(_timerBtn);
        timerBtns.Children.Add(MakeBtn("重置", TimerReset_Click));
        timerBtns.Children.Add(MakeBtn("+1 分钟", TimerPlus1_Click));
        timerStack.Children.Add(timerBtns);
        timerCard.Child = timerStack;
        left.Children.Add(timerCard);

        var honorCard = Card();
        var honorStack = new SpacedPanel { Gap = 8 };
        honorStack.Children.Add(MakeText("🏆 荣誉榜", 15, FontWeights.SemiBold, Brushes.White));
        honorStack.Children.Add(_honorList);
        honorStack.Children.Add(_honorMore);
        honorCard.Child = honorStack;
        left.Children.Add(honorCard);

        var editCard = Card();
        var editStack = new SpacedPanel { Orientation = Orientation.Horizontal, Gap = 8 };
        editStack.Children.Add(MakeBtn("＋ 小组", AddGroupBtn_Click));
        editStack.Children.Add(MakeBtn("＋ 成员", AddMemberBtn_Click));
        editStack.Children.Add(MakeBtn("删小组", RemoveGroupBtn_Click));
        editCard.Child = editStack;
        left.Children.Add(editCard);

        var easiCard = Card();
        easiCard.Child = _easiCareHint;
        left.Children.Add(easiCard);

        Grid.SetColumn(left, 0);
        main.Children.Add(left);

        // 右侧小组（可滚动）
        var scroll = new ScrollViewer
        {
            Content = _groupsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0),
        };
        Grid.SetColumn(scroll, 1);
        main.Children.Add(scroll);
        main.VerticalAlignment = VerticalAlignment.Stretch;

        // 把主区放进顶部问候下方的剩余空间
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(greetPanel, 0);
        layout.Children.Add(greetPanel);
        Grid.SetRow(main, 1);
        layout.Children.Add(main);

        _root.Children.Add(layout);

        _toastHost.Child = _toastText;
        _root.Children.Add(_toastHost);

        // 大屏关闭按钮：触摸可自行关闭，无须手机端操作
        var closeBtn = new Button
        {
            Content = "✕ 关闭看板",
            Style = (Style)Application.Current.Resources["BtnTonal"],
            FontSize = 14,
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 18, 0),
            Opacity = 0.75,
        };
        closeBtn.Click += (_, _) => CloseBoard();
        _root.Children.Add(closeBtn);

        _root.MouseDown += (_, _) => { };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) CloseBoard(); };
    }

    private static Border Card() => new()
    {
        CornerRadius = new CornerRadius(16),
        BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x2A, 0x35, 0x58)),
        BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x16, 0x1D, 0x30)),
        Padding = new Thickness(16, 14, 16, 14),
        Margin = new Thickness(0, 0, 0, 12),
    };

    private StackPanel TaskCardContent()
    {
        var stack = new SpacedPanel { Gap = 6 };
        stack.Children.Add(MakeText("📋 早读任务单", 15, FontWeights.SemiBold, Brushes.White));
        stack.Children.Add(_taskList);
        stack.Children.Add(MakeBtn("＋ 新增任务", TaskAddBtn_Click));
        var hint = MakeText("开始后按任务时长倒计时；可提前加入多个任务", 11.5, FontWeights.Normal,
            new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xBC)));
        stack.Children.Add(hint);
        return stack;
    }

    private async Task LoadWallpaperAsync()
    {
        Wallpaper.Apply(_wallpaper, opacity: 0.35, blurRadius: 34);
        await Task.CompletedTask;
    }

    private async Task InitMembersAsync()
    {
        // 首次进入：从 EasiCare 拉名册默认分成两组（教师可在 WebUI 调整）
        if (_state.Groups.Count == 0)
        {
            var students = await new EasiCareClient().GetStudentsAsync();
            if (students.Count > 0)
            {
                var half = (students.Count + 1) / 2;
                _state.AddGroup("一组");
                _state.AddGroup("二组");
                for (int i = 0; i < students.Count; i++)
                    _state.Groups[i < half ? 0 : 1].Members.Add(
                        new ReadBoardState.Member { Name = students[i] });
            }
            _state.Save();
        }
        _ = Dispatcher.BeginInvoke(new Action(RenderAll));
    }

    public static void OpenBoard()
    {
        if (Current is null) Current = new ReadBoardWindow();
        Current.ShowInternal();
    }

    /// <summary>远程打开（带操作教师身份，问候行按人显示）。</summary>
    public static void OpenBoard(string? fromUser)
    {
        if (Current is null) Current = new ReadBoardWindow();
        if (!string.IsNullOrWhiteSpace(fromUser)) Current._greetUser = fromUser;
        Current.ShowInternal();
        _ = Current.LoadGreetingAsync();
    }

    public static void CloseBoard()
    {
        if (Current is { } w) { w.Close(); Current = null; }
    }

    private void ShowInternal()
    {
        WindowUtil.EnterFullScreen(this);
        if (!IsLoaded) { Show(); Focus(); }
        else WindowUtil.ShowWithoutActivate(this);

        _tick?.Stop();
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => UpdateTimerText();
        _tick.Start();
        _topTimer.Start();
        _ = new EasiCareClient().PingAsync().ContinueWith(_ =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var pending = EasiCareClient.PendingCount;
                if (EasiCareClient.LastAvailable)
                {
                    _easiCareHint.Text = pending > 0
                        ? $"已联动 EasiCareHelper ✓ · {pending} 条评价待补写（自动重试中）"
                        : "已联动 EasiCareHelper ✓ 评价实时生效";
                }
                else
                {
                    _easiCareHint.Text = "未检测到 EasiCareHelper（评价将暂存本地）";
                }
            })));

        _ = LoadBoardConfigAsync();
        _ = LoadGreetingAsync();
    }

    /// <summary>true = 直接加花模式；false = 斜杠满比例派花（默认）。</summary>
    public async Task ReloadConfigAsync()
    {
        try
        {
            var cfg = await new ApiService().GetReadBoardConfigAsync();
            _flowerMode = cfg.Mode == "flower";
            _state.Ratio = Math.Clamp(cfg.Ratio, 1, 10);
            _ = Dispatcher.BeginInvoke(new Action(RenderAll));
        }
        catch (Exception ex) { App.Log.Aggregate("看板配置", ex); }
    }

    private Task LoadBoardConfigAsync() => ReloadConfigAsync();

    private async Task LoadGreetingAsync()
    {
        try
        {
            // 问候行按人显示：谁操作看板就问候谁（远程=操作教师；本机打开=中性问候）。
            // 不再按学科全局匹配教师（历史上所有老师打开看板都会问候同一个人）。
            if (!string.IsNullOrWhiteSpace(_greetUser))
            {
                var info = await new ApiService().GetTeacherInfoAsync(_greetUser);
                if (info is null || string.IsNullOrEmpty(info.Username))
                {
                    _greetingText.Text = "👋 您好，同学们";
                    return;
                }
                _greetingText.Text = $"👋 您好，{info.Username.Replace("老师", "")}老师";
                if (!string.IsNullOrEmpty(info.Avatar))
                {
                    var base64 = info.Avatar[(info.Avatar.IndexOf(',') + 1)..];
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new MemoryStream(Convert.FromBase64String(base64));
                    bmp.EndInit();
                    _teacherAvatar.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                    _teacherAvatar.Background = Brushes.White;
                    _teacherAvatar.Visibility = Visibility.Visible;
                }
                else
                {
                    _teacherAvatar.Visibility = Visibility.Collapsed;
                }
                return;
            }

            // 本机打开（无操作教师身份）：中性问候
            _subjectPill.Text = "今日早读 · 自由早读";
            _greetingText.Text = "👋 您好，同学们";
        }
        catch (Exception ex) { App.Log.Aggregate("看板问候", ex); }
    }

    private string? ResolveCurrentSubject()
    {
        try
        {
            var tt = SmartPreclassService.GetTimetableSnapshot();
            if (tt is null) return null;
            var now = DateTime.Now;
            var day = now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek;
            var todayKey = now.ToString("yyyy-MM-dd");
            SmartPreclassService.Lesson? best = null;
            foreach (var lesson in tt.Lessons.Where(l => l.Day == day).OrderBy(l => l.Period))
            {
                var start = SmartPreclassService.ParseStart(now, tt, lesson.Period);
                if (start is null || now < start.Value) continue;
                best = lesson;
            }
            if (best is null) best = tt.Lessons.FirstOrDefault(l => l.Day == day);
            if (best is null) return null;
            return SmartPreclassService.ResolveSubject(tt, todayKey, best.Period) ?? best.Subject;
        }
        catch { return null; }
    }

    // ---------- 渲染 ----------
    private void RenderAll()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RenderAll)); return; }
        RenderTaskList();
        var active = _state.Active();
        _timerTaskText.Text = active is { } t ? $"正在进行：{t.Name}" : "未开始";
        RenderHonor();
        RenderGroups();
    }

    private void RenderTaskList()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RenderTaskList)); return; }
        _taskList.Children.Clear();
        if (_state.Tasks.Count == 0)
        {
            _taskList.Children.Add(MakeText("暂无任务，点下方新增", 12.5, FontWeights.Normal,
                new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xBC))));
            return;
        }
        for (int i = 0; i < _state.Tasks.Count; i++)
        {
            var idx = i;
            var t = _state.Tasks[idx];
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var active = idx == _state.ActiveTask;
            var label = MakeText(
                $"{idx + 1}. {t.Name}{(t.Minutes > 0 ? $"（{t.Minutes} 分钟）" : "")}{(active ? "  ▶" : "")}",
                13.5, active ? FontWeights.Bold : FontWeights.Normal,
                active ? new SolidColorBrush(Color.FromRgb(0x7C, 0x8C, 0xFF)) : Brushes.White);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(label, 0);
            row.Children.Add(label);
            var start = MakeBtn(active && _state.TimerRunning ? "⏹" : "▶", (_, _) =>
            {
                if (active && _state.TimerRunning) _state.StopTask();
                else _state.StartTask(idx);
                _timerEnd = _state.TimerRunning ? DateTime.Now.AddMinutes(Math.Max(1, _state.TaskMinutes)) : null;
                UpdateTimerText();
                RenderAll();
                _ = _state.PublishAsync();
            });
            start.Style = (Style)Application.Current.Resources[active && _state.TimerRunning ? "BtnPrimary" : "BtnTonal"];
            start.Padding = new Thickness(8, 3, 8, 3);
            start.FontSize = 12;
            Grid.SetColumn(start, 1);
            row.Children.Add(start);
            var del = MakeBtn("✕", (_, _) =>
            {
                _state.Tasks.RemoveAt(idx);
                if (_state.ActiveTask == idx) { _state.ActiveTask = -1; _state.StopTask(); }
                else if (_state.ActiveTask > idx) _state.ActiveTask--;
                RenderAll();
                _ = _state.PublishAsync();
            });
            del.Style = (Style)Application.Current.Resources["BtnDangerSmall"];
            del.Padding = new Thickness(8, 3, 8, 3);
            del.FontSize = 12;
            Grid.SetColumn(del, 2);
            row.Children.Add(del);
            _taskList.Children.Add(row);
        }
    }

    private void TaskAddBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = TaskAddDialog();
        if (name is null) return;
        _state.Tasks.Add(new ReadBoardState.TaskItem { Name = name.Value.name, Minutes = name.Value.minutes });
        _state.Save();
        RenderAll();
        _ = _state.PublishAsync();
    }

    /// <summary>新增任务对话框：返回 (名称, 分钟) 或 null。</summary>
    private (string name, int minutes)? TaskAddDialog()
    {
        var w = new Window
        {
            Title = "＋ 新增早读任务",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["BrushSurface"],
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["AppFont"],
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        if (Owner is { IsVisible: true }) w.Owner = this;
        w.SourceInitialized += (_, _) => WindowUtil.DarkTitleBar(w);

        var stack = new StackPanel { Margin = new Thickness(22) };
        var nameBox = new System.Windows.Controls.TextBox
        {
            Style = (Style)Application.Current.Resources["InputBox"],
        };
        var minBox = new System.Windows.Controls.TextBox
        {
            Style = (Style)Application.Current.Resources["InputBox"],
            Text = "10",
        };
        stack.Children.Add(new TextBlock { Text = "任务内容", Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrushSub"], FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(nameBox);
        stack.Children.Add(new TextBlock { Text = "建议用时（分钟，0 = 不限时）", Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrushSub"], FontSize = 12, Margin = new Thickness(0, 10, 0, 6) });
        stack.Children.Add(minBox);
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        string? result = null;
        var ok = new Button { Style = (Style)Application.Current.Resources["BtnPrimary"], Content = "加入任务单" };
        ok.Click += (_, _) => { result = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : null; w.Close(); };
        w.Closed += (_, _) => { };
        btns.Children.Add(ok);
        stack.Children.Add(btns);
        w.Content = stack;
        w.Loaded += (_, _) => nameBox.Focus();
        w.ShowDialog();
        if (result is null) return null;
        var mins = Math.Clamp(int.TryParse(minBox.Text, out var mm) ? mm : 0, 0, 120);
        return (result, mins);
    }

    private void RenderHonor()
    {
        _honorList.Children.Clear();
        var ranked = _state.Groups
            .Select(g => (g, score: _state.GroupSlashes(g) + _state.GroupFlowers(g) * _state.Ratio))
            .OrderByDescending(x => x.score).ToList();
        string[] medals = { "🥇", "🥈", "🥉" };
        for (int i = 0; i < Math.Min(3, ranked.Count); i++)
            _honorList.Children.Add(MakeText(
                $"{medals[i]} {ranked[i].g.Name}  {ranked[i].score} 分", 15, FontWeights.Normal, Brushes.White));
        _honorMore.Text = ranked.Count > 3 ? $"共 {ranked.Count} 组 · 完整榜单见小组列表" : "";
    }

    private void RenderGroups()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RenderGroups)); return; }
        _groupsPanel.Children.Clear();
        foreach (var g in _state.Groups)
        {
            var slashes = _state.GroupSlashes(g);
            var flowers = _state.GroupFlowers(g);
            var card = new Border
            {
                CornerRadius = new CornerRadius(16),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x46, 0x55, 0xD6)),
                BorderThickness = new Thickness(1.5),
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1B, 0x24, 0x40)),
                Padding = new Thickness(18, 14, 18, 14),
            };
            var stack = new SpacedPanel { Gap = 8 };

            // 组头：组名 + 合计
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nameTb = MakeText(g.Name, 18, FontWeights.Bold,
                new SolidColorBrush(Color.FromRgb(0xED, 0xEF, 0xFA)));
            nameTb.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(nameTb, 0);
            head.Children.Add(nameTb);
            var totals = _flowerMode
                ? MakeText($"🌸 ×{flowers}", 16, FontWeights.SemiBold,
                    new SolidColorBrush(Color.FromRgb(0x7C, 0x8C, 0xFF)))
                : MakeText($"/ ×{slashes}    🌸 ×{flowers}", 16, FontWeights.SemiBold,
                    new SolidColorBrush(Color.FromRgb(0x7C, 0x8C, 0xFF)));
            totals.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(totals, 1);
            head.Children.Add(totals);
            stack.Children.Add(head);

            // 表头
            stack.Children.Add(MakeStudentRow(g, null, "成员", null, null, isHeader: true));

            // 学生行（按模式展示：斜杠模式仅红花数；红花模式可按人加减红花）
            for (int i = 0; i < g.Members.Count; i++)
            {
                var m = g.Members[i];
                stack.Children.Add(MakeStudentRow(g, i, m.Name, m.Slashes, m.Flowers, isHeader: false));
            }

            if (!_flowerMode && g.FlowerLog.Count > 0)
                stack.Children.Add(MakeText(
                    "最近： " + string.Join("  ", g.FlowerLog.TakeLast(3)),
                    11.5, FontWeights.Normal,
                    new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x80))));

            // 组级快速按钮（随机派发，供 WebUI 同款操作）
            var groupBtns = new SpacedPanel { Orientation = Orientation.Horizontal, Gap = 8 };
            if (!_flowerMode)
                groupBtns.Children.Add(MakeBtn("🎲 随机 +1斜杠", (_, _) => _ = ApplyAndRenderAsync(g, flowerDirect: false)));
            groupBtns.Children.Add(MakeBtn("🎲 随机 +1🌸", (_, _) => _ = ApplyAndRenderAsync(g, flowerDirect: true)));
            groupBtns.Children.Add(MakeBtn("＋ 添加成员", async (_, _) =>
            {
                var name = await Dialogs.Prompt($"向「{g.Name}」添加成员", "学生姓名");
                if (string.IsNullOrEmpty(name)) return;
                if (_state.GetMember(g, name) != null) { ShowBoardToast($"{name} 已在组内"); return; }
                g.Members.Add(new ReadBoardState.Member { Name = name });
                RenderAll();
                _ = _state.PublishAsync();
            }));
            stack.Children.Add(groupBtns);

            card.Child = stack;
            _groupsPanel.Children.Add(card);
        }
    }

    /// <summary>
    /// 学生行（模式感知）：
    /// 斜杠模式 = 序号 姓名 /×斜杠计数 [＋][－] 🌸×红花数
    /// 红花模式 = 序号 姓名 🌸×红花数 [🌸＋][🌸－] [✕]
    /// </summary>
    private Grid MakeStudentRow(ReadBoardState.Group? g, int? index, string name, int? slashes, int? flowers, bool isHeader)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2), MinHeight = isHeader ? 28 : 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        void AddToGrid(FrameworkElement el, int col)
        {
            Grid.SetColumn(el, col);
            row.Children.Add(el);
        }

        var idxText = MakeText(isHeader ? "" : (index!.Value + 1).ToString(), 14, FontWeights.Normal,
            new SolidColorBrush(Colors.Gray));
        idxText.VerticalAlignment = VerticalAlignment.Center;
        AddToGrid(idxText, 0);

        var nameTb = MakeText(name, isHeader ? 12.5 : 16,
            isHeader ? FontWeights.Normal : FontWeights.SemiBold,
            isHeader ? new SolidColorBrush(Colors.Gray)
                     : new SolidColorBrush(Color.FromRgb(0xED, 0xEF, 0xFA)));
        nameTb.VerticalAlignment = VerticalAlignment.Center;
        nameTb.TextTrimming = TextTrimming.CharacterEllipsis;
        AddToGrid(nameTb, 1);

        var btns = new SpacedPanel { Orientation = Orientation.Horizontal, Gap = 4, VerticalAlignment = VerticalAlignment.Center };

        if (_flowerMode)
        {
            // 红花模式：只展示红花计数 + 加减钮
            var flowerTb = MakeText(isHeader ? "红花" : $"🌸 ×{flowers}", isHeader ? 12.5 : 15, FontWeights.Normal,
                isHeader ? new SolidColorBrush(Colors.Gray) : new SolidColorBrush(Colors.HotPink));
            flowerTb.VerticalAlignment = VerticalAlignment.Center;
            AddToGrid(flowerTb, 2);

            if (!isHeader && g != null)
            {
                var mm = g.Members.FirstOrDefault(x => x.Name == name);
                btns.Children.Add(MakeSquareBtn("🌸＋", (_, _) => _ = ApplyStudentFlowerAsync(g, mm!, +1)));
                btns.Children.Add(MakeSquareBtn("🌸－", (_, _) => _ = ApplyStudentFlowerAsync(g, mm!, -1), 36, Colors.Gray));
                btns.Children.Add(MakeSquareBtn("✕", (_, _) =>
                {
                    _state.RemoveMember(g, mm!.Name);
                    RenderAll();
                    _ = _state.PublishAsync();
                }, 32, Colors.Gray));
            }
        }
        else
        {
            // 斜杠模式：展示斜杠计数 + 斜杠加减 + 红花计数（自动派生，无红花加减钮）
            var slashTb = MakeText(isHeader ? "斜杠" : $"/ ×{slashes}", isHeader ? 12.5 : 15, FontWeights.Normal,
                isHeader ? new SolidColorBrush(Colors.Gray)
                         : new SolidColorBrush(Color.FromRgb(0x8C, 0x9E, 0xFF)));
            slashTb.VerticalAlignment = VerticalAlignment.Center;
            AddToGrid(slashTb, 2);

            if (!isHeader && g != null)
            {
                var mm = g.Members.FirstOrDefault(x => x.Name == name);
                btns.Children.Add(MakeSquareBtn("＋", (_, _) => _ = ApplyStudentSlashAsync(g, mm!, +1)));
                btns.Children.Add(MakeSquareBtn("－", (_, _) => _ = ApplyStudentSlashAsync(g, mm!, -1)));
            }

            var flowerTb = MakeText(isHeader ? "红花" : $"🌸 ×{flowers}", isHeader ? 12.5 : 15, FontWeights.Normal,
                isHeader ? new SolidColorBrush(Colors.Gray) : new SolidColorBrush(Colors.HotPink));
            flowerTb.VerticalAlignment = VerticalAlignment.Center;
            AddToGrid(flowerTb, 3);
        }

        AddToGrid(btns, 3);

        return row;
    }

    private static Button MakeSquareBtn(string ch, RoutedEventHandler onClick, double size = 36, Color? fg = null)
    {
        var b = new Button
        {
            Content = ch,
            Width = size,
            Height = 36,
            FontSize = 16,
            Style = (Style)Application.Current.Resources["BtnTonal"],
            Padding = new Thickness(0),
        };
        if (fg.HasValue) b.Foreground = new SolidColorBrush(fg.Value);
        b.Click += onClick;
        return b;
    }

    private void UpdateTimerText()
    {
        if (!Dispatcher.CheckAccess()) return;
        if (_timerEnd is { } end && _state.TimerRunning)
        {
            var left = end - DateTime.Now;
            _timerText.Text = left <= TimeSpan.Zero
                ? "时间到 ⏰"
                : $"{(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}";
        }
        else if (_state.TimerPausedRemainingSec > 0)
        {
            var t = TimeSpan.FromSeconds(_state.TimerPausedRemainingSec);
            _timerText.Text = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }
        else _timerText.Text = "00:00:00";
    }

    /// <summary>从远程指令载荷解析目标成员：优先座号（1 起，隐私脱敏通道），兼容旧 name 字段。</summary>
    private ReadBoardState.Member? ResolveMember(ReadBoardState.Group? g, System.Text.Json.JsonElement payload)
    {
        if (g is null) return null;
        if (payload.TryGetProperty("seat", out var seatEl) && seatEl.TryGetInt32(out var seat))
        {
            var list = g.Members;
            if (seat >= 1 && seat <= list.Count) return list[seat - 1];
            return null;
        }
        if (payload.TryGetProperty("name", out var nm))
            return _state.GetMember(g, nm.GetString() ?? "");
        return null;
    }

    // ---------- 远程控制入口（App 路由调用，UI 线程） ----------
    public void ApplyCommand(string action, string rawPayload, string? fromUser = null)
    {
        if (!string.IsNullOrWhiteSpace(fromUser) && _greetUser != fromUser)
        {
            _greetUser = fromUser;
            _ = LoadGreetingAsync();
        }
        using var payloadDoc = System.Text.Json.JsonDocument.Parse(
            string.IsNullOrWhiteSpace(rawPayload) ? "{}" : rawPayload);
        var payload = payloadDoc.RootElement;
        switch (action)
        {
            case "settask": // 兼容旧版 WebUI：单任务设置
                _state.CurrentTask = payload.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                _state.TaskMinutes = payload.TryGetProperty("minutes", out var mm) ? mm.GetInt32() : 0;
                if (_state.Tasks.Count == 0)
                    _state.Tasks.Add(new ReadBoardState.TaskItem { Name = _state.CurrentTask, Minutes = _state.TaskMinutes });
                else { _state.Tasks[0].Name = _state.CurrentTask; _state.Tasks[0].Minutes = _state.TaskMinutes; }
                _state.ActiveTask = 0;
                if (_state.TaskMinutes > 0)
                {
                    _state.TimerRunning = true;
                    _state.TimerStartedAt = DateTime.Now;
                    _timerEnd = DateTime.Now.AddMinutes(_state.TaskMinutes);
                }
                break;
            case "addtask":
            {
                var name = payload.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                var minutes = payload.TryGetProperty("minutes", out var tm) ? tm.GetInt32() : 0;
                if (name.Length > 0 && _state.Tasks.Count < 12)
                    _state.Tasks.Add(new ReadBoardState.TaskItem { Name = name, Minutes = Math.Clamp(minutes, 0, 180) });
                break;
            }
            case "removetask":
            {
                var idx = payload.TryGetProperty("index", out var ri) ? ri.GetInt32() : -1;
                if (idx >= 0 && idx < _state.Tasks.Count)
                {
                    _state.Tasks.RemoveAt(idx);
                    if (_state.ActiveTask == idx) { _state.ActiveTask = -1; _state.StopTask(); _timerEnd = null; }
                    else if (_state.ActiveTask > idx) _state.ActiveTask--;
                }
                break;
            }
            case "starttask":
            {
                var idx = payload.TryGetProperty("index", out var si) ? si.GetInt32() : -1;
                if (idx >= 0 && idx < _state.Tasks.Count)
                {
                    _state.StartTask(idx);
                    _timerEnd = _state.TimerRunning && _state.TaskMinutes > 0
                        ? DateTime.Now.AddMinutes(_state.TaskMinutes) : null;
                }
                break;
            }
            case "stoptask":
                _state.StopTask();
                _timerEnd = null;
                break;
            case "autogroup":
            {
                var size = payload.TryGetProperty("size", out var sz) ? sz.GetInt32() : 0;
                if (size >= 2) _state.AutoGroup(size);
                break;
            }
            case "timer":
                var cmd = payload.TryGetProperty("cmd", out var c) ? c.GetString() : "";
                if (cmd == "pause")
                {
                    _state.TimerRunning = false;
                    if (_timerEnd is { } e2) _state.TimerPausedRemainingSec = Math.Max(0, (int)(e2 - DateTime.Now).TotalSeconds);
                }
                else if (cmd == "resume")
                {
                    _state.TimerRunning = true;
                    _timerEnd = DateTime.Now.AddSeconds(_state.TimerPausedRemainingSec);
                }
                else if (cmd == "reset")
                {
                    _state.TimerRunning = false;
                    _timerEnd = null;
                    _state.TimerPausedRemainingSec = 0;
                }
                break;
            case "addslash":
            {
                var g = _state.GetGroup(payload.TryGetProperty("group", out var gn) ? gn.GetString() ?? "" : "");
                var m = ResolveMember(g, payload);
                if (m != null) _ = ApplyStudentSlashAsync(g!, m, delta: +1);
                else if (g != null) _ = ApplyAndRenderAsync(g, flowerDirect: false);
                break;
            }
            case "removeslash":
            {
                var g = _state.GetGroup(payload.TryGetProperty("group", out var gn3) ? gn3.GetString() ?? "" : "");
                var m = ResolveMember(g, payload);
                if (m != null) { _state.RemoveSlashFromStudent(g!, m); ShowBoardToast($"斜杠 −1 → {m.Name}"); }
                break;
            }
            case "flower":
            case "addflower":
            {
                var g = _state.GetGroup(payload.TryGetProperty("group", out var gn2) ? gn2.GetString() ?? "" : "");
                var m = ResolveMember(g, payload);
                if (m != null) _ = ApplyStudentFlowerAsync(g!, m, delta: +1);
                else if (g != null) _ = ApplyAndRenderAsync(g, flowerDirect: true);
                break;
            }
            case "removeflower":
            {
                var g = _state.GetGroup(payload.TryGetProperty("group", out var gn4) ? gn4.GetString() ?? "" : "");
                var m = ResolveMember(g, payload);
                if (m != null) { _state.RemoveFlowerFromStudent(g!, m); ShowBoardToast($"红花 −1 → {m.Name}"); }
                break;
            }
            case "addmember":
            {
                var g = _state.GetGroup(payload.TryGetProperty("group", out var gn5) ? gn5.GetString() ?? "" : "");
                var name = payload.TryGetProperty("name", out var nm5) ? nm5.GetString() ?? "" : "";
                if (g != null && name.Length > 0 && _state.GetMember(g, name) is null)
                {
                    g.Members.Add(new ReadBoardState.Member { Name = name });
                    ShowBoardToast($"已添加 {name} → {g.Name}");
                }
                break;
            }
            case "removemember":
            {
                var g = _state.GetGroup(payload.TryGetProperty("group", out var gn6) ? gn6.GetString() ?? "" : "");
                var m = ResolveMember(g, payload);
                if (m != null) { _state.RemoveMember(g!, m.Name); ShowBoardToast($"已移除 {m.Name}"); }
                break;
            }
            case "addgroup":
            {
                var name = payload.TryGetProperty("name", out var gn7) ? gn7.GetString() ?? "" : "";
                if (name.Length > 0 && _state.GetGroup(name) is null)
                {
                    _state.AddGroup(name);
                    ShowBoardToast($"已添加小组 {name}");
                }
                break;
            }
            case "removegroup":
            {
                var g2 = _state.GetGroup(payload.TryGetProperty("group", out var gn8) ? gn8.GetString() ?? "" : "");
                if (g2 != null)
                {
                    _state.RemoveGroup(g2.Name);
                    ShowBoardToast($"已删除小组 {g2.Name}");
                }
                break;
            }
            case "close":
                CloseBoard();
                return;
        }
        RenderAll();
        _ = _state.PublishAsync();
    }

    private async Task ApplyStudentSlashAsync(ReadBoardState.Group g, ReadBoardState.Member m, int delta)
    {
        if (delta >= 0)
        {
            var recipients = await _state.AddSlashToStudentAsync(g, m);
            ShowBoardToast(recipients.Count > 0
                ? "🌸 认真读书评价 → " + string.Join("、", recipients.Select(r => r.Name))
                : $"斜杠 +1 → {m.Name}");
        }
        else
        {
            _state.RemoveSlashFromStudent(g, m);
            ShowBoardToast($"斜杠 −1 → {m.Name}");
        }
        RenderAll();
        await _state.PublishAsync();
    }

    private async Task ApplyStudentFlowerAsync(ReadBoardState.Group g, ReadBoardState.Member m, int delta)
    {
        if (delta >= 0)
        {
            var ok = await _state.AddFlowerToStudentAsync(g, m);
            ShowBoardToast(ok
                ? $"🌸 认真读书评价已写入 → {m.Name}"
                : $"🌸 红花 +1 → {m.Name}（{EasiCareClient.LastEvalStatus}）");
        }
        else
        {
            _state.RemoveFlowerFromStudent(g, m);
            ShowBoardToast($"红花 −1 → {m.Name}");
        }
        RenderAll();
        await _state.PublishAsync();
    }

    private void ShowBoardToast(string text)
    {
        _toastText.Text = text;
        _toastHost.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toastText.Text = ""; _toastHost.Visibility = Visibility.Collapsed; };
        _toastTimer.Start();
    }

    private async Task ApplyAndRenderAsync(ReadBoardState.Group g, bool flowerDirect)
    {
        List<ReadBoardState.Member> flowerRecipients;
        ReadBoardState.Member member;
        if (flowerDirect)
        {
            // 直接加花模式：+1 朵即按占比派给一人并写评价
            flowerRecipients = await _state.AddFlowersAsync(g, 1);
            member = flowerRecipients.FirstOrDefault()!;
            ShowBoardToast("🌸 认真读书评价 → " + (flowerRecipients.Count > 0
                ? string.Join("、", flowerRecipients.Select(r => r.Name)) : "—"));
        }
        else
        {
            (member, flowerRecipients) = await _state.AddSlashAsync(g);
            ShowBoardToast(flowerRecipients.Count > 0
                ? "🌸 认真读书评价 → " + string.Join("、", flowerRecipients.Select(r => r.Name))
                : $"斜杠 +1 → {member.Name}");
        }
        RenderAll();
        await _state.PublishAsync();
    }

    // ---------- 本地交互 ----------
    private void TimerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_state.TimerRunning)
        {
            _state.TimerRunning = false;
            if (_timerEnd is { } e2) _state.TimerPausedRemainingSec = Math.Max(0, (int)(e2 - DateTime.Now).TotalSeconds);
            _timerBtn.Content = "继续";
        }
        else
        {
            _state.TimerRunning = true;
            _timerEnd = DateTime.Now.AddSeconds(_state.TimerPausedRemainingSec > 0
                ? _state.TimerPausedRemainingSec : Math.Max(1, _state.TaskMinutes) * 60);
            _timerBtn.Content = "暂停";
        }
        UpdateTimerText();
    }

    private void TimerReset_Click(object sender, RoutedEventArgs e)
    {
        _state.TimerRunning = false;
        _timerEnd = null;
        _state.TimerPausedRemainingSec = 0;
        _timerBtn.Content = "暂停";
        UpdateTimerText();
    }

    private void TimerPlus1_Click(object sender, RoutedEventArgs e)
    {
        if (_timerEnd is { } end && _state.TimerRunning)
        {
            _timerEnd = end.AddMinutes(1);
            _state.TaskMinutes += 1;
            ShowBoardToast("已延长 1 分钟");
        }
        else if (_state.TaskMinutes > 0)
        {
            _state.TaskMinutes += 1;
            _taskHint.Text = $"建议用时 {_state.TaskMinutes} 分钟";
        }
        RenderAll();
        _ = _state.PublishAsync();
    }

    private async void TaskEditBtn_Click(object sender, RoutedEventArgs e)
    {
        var task = await Dialogs.Prompt("修改早读任务", "例如：背诵《背影》第二段", _state.CurrentTask);
        if (task is null) return;
        var minutes = await Dialogs.Prompt("建议用时", "分钟数（0 = 不限时）",
            _state.TaskMinutes.ToString());
        _state.CurrentTask = task.Trim();
        _state.TaskMinutes = Math.Max(0, int.TryParse(minutes ?? "0", out var mm) ? mm : 0);
        if (_state.TaskMinutes > 0)
        {
            _state.TimerRunning = true;
            _state.TimerStartedAt = DateTime.Now;
            _timerEnd = DateTime.Now.AddMinutes(_state.TaskMinutes);
        }
        RenderAll();
        _ = _state.PublishAsync();
    }

    private async void AddGroupBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = await Dialogs.Prompt("添加小组", "小组名称，如：第三组");
        if (string.IsNullOrEmpty(name)) return;
        _state.AddGroup(name);
        _state.Save();
        RenderAll();
        _ = _state.PublishAsync();
    }

    private async void AddMemberBtn_Click(object sender, RoutedEventArgs e)
    {
        var r = await Dialogs.Prompt2("添加成员", "加入哪个小组（如：一组）", "学生姓名");
        if (r is null) return;
        var (groupName, name) = r.Value;
        var g = _state.GetGroup(groupName ?? "");
        if (g is null || string.IsNullOrEmpty(name))
        {
            ShowBoardToast("小组不存在或姓名为空");
            return;
        }
        if (g.Members.Any(m => m.Name == name)) { ShowBoardToast("该成员已在组内"); return; }
        g.Members.Add(new ReadBoardState.Member { Name = name });
        _state.Save();
        RenderAll();
        _ = _state.PublishAsync();
    }

    private async void RemoveGroupBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = await Dialogs.Prompt("删除小组", "要删除的小组名称（成员与计数一并删除）");
        if (string.IsNullOrEmpty(name)) return;
        _state.RemoveGroup(name);
        _state.Save();
        RenderAll();
        _ = _state.PublishAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _tick?.Stop();
        _toastTimer?.Stop();
        _topTimer.Stop();
        _easiCareRetry.Stop();
        if (Current == this) Current = null;
        base.OnClosed(e);
    }
}
