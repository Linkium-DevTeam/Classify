using System.Diagnostics;

namespace Classify.Services;

/// <summary>
/// 受限机器看护（伴生进程互拉）：计划任务/注册表都失败时（机房还原卡、组策略限制），
/// 主进程拉起伴生进程（同一 exe，--companion 参数，无窗口），双方互为监视：
/// 主进程被杀 → 伴生 15 秒内拉起；伴生死 → 主进程重生。
/// 主动退出（托盘退出）通过命名事件握手，伴生不会误拉。
/// 伴生还承担自动更新的文件换装（UpdateSwap）：主进程下载校验后退出，伴生完成复制并拉起新版。
/// </summary>
public static class Companion
{
    public const string CompanionMutexName = @"Local\Classify_Companion";
    public const string ExitEventName = @"Local\Classify_ExitRequested";

    /// <summary>伴生进程主循环（Program.Main 直入，不初始化 WPF）。</summary>
    public static int Run()
    {
        using var mutex = new Mutex(true, CompanionMutexName, out var isNew);
        if (!isNew) return 0; // 已有伴生：避免双看护进程重复拉起

        try { App.Log.Init(); } catch { /* 日志失败不影响看护 */ }
        App.Log.Info("伴生进程启动");

        using var exitEvent = OpenExitEvent();
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return 0;

        while (true)
        {
            try
            {
                if (exitEvent.WaitOne(0)) return 0;

                // 更新换装优先：有 pending 标记就先换装（换装后主进程互斥体已释放，下方逻辑自然拉起新版）
                if (UpdateSwap.Pending())
                {
                    var ok = UpdateSwap.Apply(m => App.Log.Info("[swap] " + m));
                    if (!ok) App.Log.Error("更新换装未完成，稍后重试", null);
                }

                var mainAlive = false;
                try { using var _ = Mutex.OpenExisting(Program.MainMutexName); mainAlive = true; }
                catch (WaitHandleCannotBeOpenedException) { mainAlive = false; }
                catch (AbandonedMutexException) { mainAlive = true; }
                catch { mainAlive = true; } // 保守：不确定时不动

                if (!mainAlive)
                {
                    if (exitEvent.WaitOne(0)) return 0;
                    try
                    {
                        Process.Start(new ProcessStartInfo(exe, "--autostart") { UseShellExecute = true });
                    }
                    catch (Exception ex) { App.Log.Error("伴生拉起主进程失败", ex); }
                }

                // 有更新待换装时缩短轮询间隔；复制失败也给伴生留出退避时间，避免 3 秒一次死循环
                Thread.Sleep(UpdateSwap.Pending() ? 10000 : 15000);
            }
            catch (Exception ex)
            {
                try { App.Log.Error("伴生循环异常", ex); } catch { }
                try { Thread.Sleep(15000); } catch { return 0; }
            }
        }
    }

    /// <summary>主进程确保伴生在运行；未运行则拉起。</summary>
    public static void EnsureRunning()
    {
        try
        {
            using var _ = Mutex.OpenExisting(CompanionMutexName);
            return; // 伴生健在
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch { return; } // 异常时不重复拉起

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                Process.Start(new ProcessStartInfo(exe, "--companion") { UseShellExecute = true, CreateNoWindow = true });
            App.Log.Info("伴生看护进程已拉起");
        }
        catch (Exception ex) { App.Log.Error("拉起伴生进程失败", ex); }
    }

    /// <summary>主动退出前通知伴生：不要拉起。</summary>
    public static void SignalExit()
    {
        try
        {
            using var ev = OpenExitEvent();
            ev.Set();
        }
        catch { }
    }

    private static EventWaitHandle OpenExitEvent()
    {
        // 主进程在启动时会创建并复位该事件；伴生打开既有实例即可
        try { return EventWaitHandle.OpenExisting(ExitEventName); }
        catch (WaitHandleCannotBeOpenedException)
        {
            return new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName);
        }
    }
}
