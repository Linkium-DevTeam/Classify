using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Shell;
using Classify.Models;
using Classify.Services;
using Classify.UI;

namespace Classify;

/// <summary>
/// 应用壳：启动服务、消息路由、窗口生命周期。
/// WPF 自带 DispatcherSynchronizationContext，await 续体天然回 UI 线程（WinUI3 时代的 0x8001010E 一类问题不存在）。
/// </summary>
public partial class App : Application
{
    public static SettingsService Settings { get; private set; } = null!;
    public static LogService Log { get; } = new();
    public static ObservableCollection<ActivityItem> Activities { get; } = new();

    public bool SilentStart { get; init; }
    public bool OpenRequested { get; init; }

    private static MainWindow? _mainWindow;
    private static WizardWindow? _wizardWindow;
    private static bool _servicesStarted;

    public static App Inst => (App)Current;
    public static System.Windows.Threading.Dispatcher Ui => Current.Dispatcher;

    /// <summary>主窗口句柄（任务栏进度等 Win32 调用需要）。</summary>
    public static IntPtr MainHwnd => _mainWindow?.Handle ?? IntPtr.Zero;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Init();
        Log.Info(SilentStart ? "应用启动（开机静默自启）" : "应用启动");
        Settings = SettingsService.Load();
        ServerEndpoints.InitFrom(Settings.Values.ServerUrl);
        Log.Info($"设置加载: WizardDone={Settings.Values.WizardDone} path={Settings.FilePath}");

        // 任何未处理异常都落日志，便于现场排查
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("UI 线程未处理异常: " + ex.Exception?.Message, ex.Exception);
            ex.Handled = true; // 主线程存活优先：记录后继续运行
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Error("进程级未处理异常", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Aggregate("未观察任务异常", ex.Exception);
            ex.SetObserved();
        };

        InitJumpList();

        if (!Settings.Values.WizardDone)
        {
            _wizardWindow = new WizardWindow();
            _wizardWindow.Show();
            return;
        }

        if (SilentStart && !OpenRequested)
        {
            StartServices();
            _mainWindow = new MainWindow(); // 仅驻留（不 Show），托盘可随时唤起
            Log.Info("静默自启完成，已驻留托盘");
            return;
        }

        ShowMainWindow();
    }

    private static bool _jumpListDone;

    private static void InitJumpList()
    {
        if (_jumpListDone) return;
        _jumpListDone = true;
        try
        {
            var exe = Environment.ProcessPath ?? "";
            var jl = new JumpList();
            jl.JumpItems.Add(new JumpTask
            {
                Title = "打开主界面",
                Description = "显示 Classify 主界面",
                ApplicationPath = exe,
                Arguments = "--open",
            });
            jl.JumpItems.Add(new JumpTask
            {
                Title = "设置",
                Description = "打开设置",
                ApplicationPath = exe,
                Arguments = "--open --tab=settings",
            });
            JumpList.SetJumpList(Current, jl);
            jl.Apply();
        }
        catch (Exception ex) { Log.Aggregate("JumpList", ex); }
    }

    public static void ShowMainWindow(string? tab = null)
    {
        StartServices();
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Show();
        }
        else
        {
            _mainWindow.BringToFront();
        }
        if (tab != null) _mainWindow.NavigateTo(tab);
    }

    /// <summary>显示主界面并跳到指定导航项（home/settings/about）。</summary>
    public static void ShowMainWindowTab(string tag)
    {
        Ui.BeginInvoke(new Action(() =>
        {
            try { ShowMainWindow(tag); }
            catch (Exception ex) { Log.Error("导航失败", ex); }
        }));
    }

    public static void TryShowMainWindow()
    {
        Ui.BeginInvoke(new Action(() =>
        {
            try { ShowMainWindow(); }
            catch (Exception ex) { Log.Error("唤起主窗口失败", ex); }
        }));
    }

    /// <summary>计划内退出（含系统关机）都清掉运行标记，避免下次启动误报崩溃。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try { Settings?.Save(); } catch { }
        MetricsService.MarkCleanExit();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        MetricsService.MarkCleanExit();
        base.OnSessionEnding(e);
    }

    public static void OnWizardFinished()
    {
        var wizard = _wizardWindow;
        _wizardWindow = null;
        Ui.BeginInvoke(new Action(() =>
        {
            try { ShowMainWindow(); }
            catch (Exception ex) { Log.Error("向导完成后显示主窗口失败", ex); }
            try { wizard?.Close(); } catch (Exception ex) { Log.Error("关闭向导窗口失败", ex); }
        }));
    }

    public static void NotifyMainWindowClosed() => _mainWindow = null;

    private static void StartServices()
    {
        if (_servicesStarted) return;
        _servicesStarted = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        MessageRouter.Init();
        Log.Info($"启动阶段1（消息路由）{sw.ElapsedMilliseconds}ms");
        _ = Task.Run(async () => // 非关键路径延迟初始化
        {
            try
            {
                AutoStartService.WriteWatchdogVbs();
                if (AutoStartService.IsEnabled) Companion.EnsureRunning();
                await Task.Delay(1500);
                Log.Info($"启动阶段3（看门狗/伴生）{sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex) { Log.Error("延迟初始化失败", ex); }
        });

        ClassDetectService.Instance.Start();
        AnnouncementService.Instance.Init();
        TrayService.Instance.Init();
        if (Settings.Values.IslandEnabled)
        {
            try { IslandWindow.ShowDefault(); }
            catch (Exception ex) { Log.Error("灵动岛启动失败", ex); }
        }
        Log.Info($"启动阶段2（检测/喊话/托盘）{sw.ElapsedMilliseconds}ms");
        UpdateService.Start();
        SmartPreclassService.Start();
        BroadcastService.Start();
        MetricsService.Start();

        // 硬件指纹设备 ID（16 位，重装不变；首次计算后写入设置）
        HwIdService.EnsureStored();

        // 加载 EasiCare 评价缓存队列（离线/令牌过期时缓存的待写入评价）
        if (SettingsService.Instance.Values.EasiCareEnabled) EasiCareClient.LoadPending();

        // 匿名遥测由 MetricsService 负责（事件计数每日聚合 + 崩溃检测）

        if (!string.IsNullOrWhiteSpace(Settings.Values.ServerUrl)
            && !string.IsNullOrEmpty(Settings.Values.DeviceToken))
        {
            PollingService.Instance.Start();
        }
    }

    /// <summary>统一消息入口：云端轮询 / WebSocket。</summary>
    public static class MessageRouter
    {
        private static bool _inited;

        public static void Init()
        {
            if (_inited) return;
            _inited = true;
            PollingService.Instance.MessageReceived += OnMessage;
        }

        private static void OnMessage(InboxMessage msg)
        {
            Log.Info($"收到消息 type={msg.Type} from={msg.From}");
            MetricsService.Incr("msg_" + msg.Type);
            AddActivity(msg);
            switch (msg.Type)
            {
                case "text":
                    AnnouncementService.Instance.Enqueue(new AnnItem
                    {
                        Mid = msg.Mid,
                        Text = msg.Text ?? "",
                        Speak = msg.Speak,
                        From = msg.From ?? "",
                    });
                    break;
                case "file":
                    _ = FileReceiveService.Instance.HandleAsync(msg);
                    break;
                case "cmd":
                    _ = CommandService.Instance.ExecuteAsync(msg.CmdName, msg.CmdValue)
                        .ContinueWith(_ => ReceiptService.Send(msg.Mid, "displayed"),
                            TaskScheduler.Default);
                    break;
                case "clipboard":
                    // 剪贴板同步已下线（使用率低，由图片快投替代）——忽略旧指令
                    break;
                case "clock":
                    Ui.BeginInvoke(new Action(() =>
                    {
                        var action = msg.CmdName; // payload.action 已映射到 CmdName
                        if (action == "stop")
                        {
                            ClockWindow.StopClock();
                            ReceiptService.Send(msg.Mid, "displayed");
                        }
                        else
                        {
                            ClockWindow.StartClock();
                            if (int.TryParse(msg.CmdValue, out var mins) && mins > 0)
                                ClockWindow.Current?.SetCountdown(mins, "倒计时");
                            ReceiptService.Send(msg.Mid, "displayed");
                        }
                    }));
                    break;
                case "camera":
                    // 隐私默认关闭：未开启时拒答并引导（能力位已同步 WebUI，正常不会发来）
                    if (!Settings.Values.AllowCamera)
                    {
                        ReceiptService.Send(msg.Mid, "failed");
                        Ui.BeginInvoke(new Action(() =>
                            ToastWindow.ShowToast("未开启拍照回传",
                                "出于隐私考虑此功能默认关闭，可在 设置 → 权限与隐私 中开启")));
                        break;
                    }
                    ReceiptService.Send(msg.Mid, "received");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await CameraService.CaptureAndUploadAsync();
                            ReceiptService.Send(msg.Mid, "displayed");
                            _ = Ui.BeginInvoke(new Action(() =>
                                ToastWindow.ShowToast("📷 照片已回传", "可在手机端文件列表查看")));
                        }
                        catch (Exception ex)
                        {
                            ReceiptService.Send(msg.Mid, "failed");
                            Log.Error("拍照回传失败", ex);
                            _ = Ui.BeginInvoke(new Action(() =>
                                ToastWindow.ShowToast("拍照失败", ex.Message)));
                        }
                    });
                    break;
                case "readboard":
                    Ui.BeginInvoke(new Action(() =>
                    {
                        if (msg.CmdName == "close") { ReadBoardWindow.CloseBoard(); return; }
                        if (ReadBoardWindow.Current is null)
                        {
                            // 手机端远程操作（含名册编辑）在看板未开时自动开板，避免指令被静默丢弃
                            ReadBoardWindow.OpenBoard(msg.From);
                        }
                        if (msg.CmdName == "open")
                        {
                            ReceiptService.Send(msg.Mid, "displayed");
                            return;
                        }
                        ReadBoardWindow.Current?.ApplyCommand(msg.CmdName, msg.RawPayload, msg.From);
                        ReceiptService.Send(msg.Mid, "displayed");
                    }));
                    break;
                case "screenshot":
                    ReceiptService.Send(msg.Mid, "received");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var b = Interop.Win32.MonitorBoundsOfWindow(IntPtr.Zero);
                            var path = Path.Combine(Path.GetTempPath(),
                                $"classify-screen-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
                            if (!Interop.ScreenCapture.CaptureToJpeg(path, b.Left, b.Top,
                                b.Right - b.Left, b.Bottom - b.Top, 70))
                                throw new InvalidOperationException("屏幕捕获失败");
                            await new ApiService().UploadToTeacherAsync(path,
                                $"教室画面-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
                            try { File.Delete(path); } catch { }
                            ReceiptService.Send(msg.Mid, "displayed");
                            _ = Ui.BeginInvoke(new Action(() =>
                                ToastWindow.ShowToast("🖥️ 屏幕画面已回传", "可在手机端文件列表查看")));
                        }
                        catch (Exception ex)
                        {
                            ReceiptService.Send(msg.Mid, "failed");
                            Log.Error("截图回传失败", ex);
                        }
                    });
                    break;
                case "preclass":
                    Ui.BeginInvoke(new Action(() =>
                    {
                        var mode = msg.CmdName;
                        var subject = "";
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(
                                string.IsNullOrWhiteSpace(msg.RawPayload) ? "{}" : msg.RawPayload);
                            if (doc.RootElement.TryGetProperty("subject", out var sj))
                                subject = sj.GetString() ?? "";
                        }
                        catch { }
                        if (mode == "override-now" && !string.IsNullOrWhiteSpace(subject))
                            _ = SmartPreclassService.RunForSubjectAsync(subject, "手机指定学科候课");
                        else
                            _ = SmartPreclassService.RunNowAsync();
                    }));
                    break;
                case "image":
                {
                    ReceiptService.Send(msg.Mid, "received");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string fid = "", name = "投屏图片";
                            try
                            {
                                using var doc = System.Text.Json.JsonDocument.Parse(
                                    string.IsNullOrWhiteSpace(msg.RawPayload) ? "{}" : msg.RawPayload);
                                if (doc.RootElement.TryGetProperty("fid", out var fe))
                                    fid = fe.GetString() ?? "";
                                if (doc.RootElement.TryGetProperty("name", out var ne))
                                    name = ne.GetString() ?? name;
                            }
                            catch { }
                            if (string.IsNullOrEmpty(fid))
                            {
                                ReceiptService.Send(msg.Mid, "failed");
                                return;
                            }
                            var dir = Path.Combine(AppPaths.DataDir, "cast");
                            Directory.CreateDirectory(dir);
                            // 清理上一批投屏图，避免堆积
                            foreach (var old in Directory.GetFiles(dir))
                                try { File.Delete(old); } catch { }
                            var ext = Path.GetExtension(name);
                            if (string.IsNullOrEmpty(ext)) ext = ".png";
                            var path = Path.Combine(dir, $"cast-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
                            await new ApiService().DownloadFileAsync(fid, path);
                            _ = Ui.BeginInvoke(new Action(() =>
                            {
                                ImageShowWindow.Enqueue(path);
                                ReceiptService.Send(msg.Mid, "displayed");
                            }));
                        }
                        catch (Exception ex)
                        {
                            ReceiptService.Send(msg.Mid, "failed");
                            Log.Error("图片快投失败", ex);
                        }
                    });
                    break;
                }
                case "picker":
                    ReceiptService.Send(msg.Mid, "received");
                    Ui.BeginInvoke(new Action(() =>
                    {
                        if (PickerWindow.TryStart())
                            ReceiptService.Send(msg.Mid, "displayed");
                        else
                            ReceiptService.Send(msg.Mid, "failed");
                    }));
                    break;
                case "notice":
                    // 教师发布/清除常驻通知：立即刷新灵动岛（服务端已按设备聚合全部绑定教师）
                    ReceiptService.Send(msg.Mid, "displayed");
                    Ui.BeginInvoke(new Action(() => IslandWindow.Refresh()));
                    break;
                case "readboard-config":
                    // 看板加分规则变更 → 在线看板即时刷新配置
                    Ui.BeginInvoke(new Action(
                        () => _ = ReadBoardWindow.Current?.ReloadConfigAsync()));
                    break;
            }
        }

        private static void AddActivity(InboxMessage msg)
        {
            Ui.BeginInvoke(new Action(() =>
            {
                Activities.Insert(0, new ActivityItem
                {
                    Icon = msg.Type switch
                    {
                        "text" => "📢",
                        "file" => "🗂️",
                        "cmd" => "🎛️",
                        _ => "✨",
                    },
                    Title = msg.Type switch
                    {
                        "text" => (msg.Text?.Length > 40 ? msg.Text[..40] + "…" : msg.Text) ?? "",
                        "file" => $"收到文件 {msg.FileName}",
                        "cmd" => $"系统控制：{msg.CmdName}",
                        _ => msg.Type,
                    },
                    Detail = $"{msg.From} · {DateTime.Now:HH:mm:ss}",
                });
                while (Activities.Count > 30) Activities.RemoveAt(Activities.Count - 1);
            }));
        }
    }
}
