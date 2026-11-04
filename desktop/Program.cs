using System.Diagnostics;
using System.Threading;
using System.Windows;
using Classify.Services;

namespace Classify;

/// <summary>
/// 进程入口：伴生看护 → 单实例互斥 → WPF 应用。
/// 命令行：--companion（伴生进程）/ --autostart、--minimized、--silent（静默自启）/ --open（跳链唤起主界面）。
/// </summary>
public static class Program
{
    public const string MainMutexName = @"Local\Classify_SingleInstance";
    private const string EventName = @"Local\Classify_ShowMain";

    [STAThread]
    public static int Main(string[] args)
    {
        DiagnosticsService.Boot("==== 进程启动 " + Environment.Version + " · " +
                                Environment.OSVersion.VersionString + " · " +
                                (Environment.Is64BitProcess ? "x64" : "x86") + " ====");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            DiagnosticsService.Boot("未处理异常: " +
                ((e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString() ?? "?"));
            DiagnosticsService.FlushSync();
        };
        try
        {
            return MainCore(args);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Boot("FATAL: " + ex);
            DiagnosticsService.FlushSync();
            throw;
        }
    }

    private static int MainCore(string[] args)
    {
        // 伴生看护进程必须先于单实例互斥处理（否则会被单实例逻辑立即退出）
        if (args.Any(a => a.Equals("--companion", StringComparison.OrdinalIgnoreCase)))
        {
            DiagnosticsService.Boot("阶段：伴生进程");
            Interop.PowerThrottling.DisableEcoQoS();
            return Companion.Run();
        }

        // 启动前消费待换装的更新（伴生缺席时的兜底；正常路径由伴生完成）。
        // 换装后以新版重启自身；认领失败（伴生正在换装）则照常继续。
        if (UpdateSwap.Pending())
        {
            DiagnosticsService.Boot("阶段：消费待换装更新");
            if (UpdateSwap.Apply(m => DiagnosticsService.Boot("[swap] " + m)))
            {
                DiagnosticsService.Boot("换装完成，以新版重启自身");
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(exe, "--autostart") { UseShellExecute = true });
                        return 0;
                    }
                    catch (Exception ex) { DiagnosticsService.Boot("新版重启失败: " + ex.Message); }
                }
            }
        }

        DiagnosticsService.Boot("阶段：单实例互斥");
        using var mutex = new Mutex(true, MainMutexName, out var isNew);
        if (!isNew)
        {
            // 已有实例在跑：只有用户主动启动（无静默参数）才唤起主界面。
            // 开机双通道自启（计划任务 + Run 项）同时拉起时，后到的一方直接退出，
            // 否则会把静默驻留的实例主界面弹出来（用户反馈过"开机自启打开主界面"）。
            bool silentLaunch = args.Any(a => a is "--autostart" or "--minimized" or "--silent");
            if (!silentLaunch)
            {
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(EventName);
                    ev.Set();
                    DiagnosticsService.Boot("已有实例，唤起后退出");
                }
                catch { /* 首实例尚未创建事件 */ }
            }
            return 0;
        }

        SilentStart = args.Any(a => a is "--autostart" or "--minimized" or "--silent");
        bool openRequested = args.Any(a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));

        // 关闭 Win11 效率模式节流：保证托盘驻留时轮询/长连接不被拖慢
        DiagnosticsService.Boot("阶段：PowerThrottling");
        Interop.PowerThrottling.DisableEcoQoS();

        // 复位退出握手事件（上次主动退出设置的），伴生进程据此继续工作
        _ = new EventWaitHandle(false, EventResetMode.ManualReset, Companion.ExitEventName);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        ThreadPool.RegisterWaitForSingleObject(
            showEvent,
            (_, _) => App.TryShowMainWindow(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        DiagnosticsService.Boot("阶段：创建 App（设置/日志系统初始化）");
        var app = new App
        {
            SilentStart = SilentStart,
            OpenRequested = openRequested,
        };
        app.InitializeComponent();
        DiagnosticsService.Boot("阶段：Run");
        app.Run();
        DiagnosticsService.Boot("阶段：正常退出");
        return 0;
    }

    public static bool SilentStart { get; private set; }
}
