using Microsoft.Win32;

namespace Classify.Services;

/// <summary>
/// 开机自启：
///  1) 安装器（管理员）已创建 Classify 计划任务（登录静默自启）——存在与否通过任务缓存注册表只读探测；
///  2) 应用内开关管理 HKCU Run 注册表项（无权限要求，必成）。
/// 两者并存时双通道自启，由单实例互斥去重。被杀恢复由伴生互拉（Companion）承担。
/// v3 不在应用内调用 schtasks/powershell 等外部命令（权限不稳且告警面大）。
/// </summary>
public static class AutoStartService
{
    public const string TaskName = "Classify";
    private const string RunValueName = "Classify";

    private static string ExePath =>
        Environment.ProcessPath ?? AppContext.BaseDirectory + "Classify.exe";

    private static string RunValue =>
        "\"" + ExePath + "\" --autostart";

    private static string WatchdogVbsPath => Path.Combine(
        AppPaths.DataDir, "watchdog.vbs");

    public static string CurrentMethod =>
        SettingsService.Instance.Values.AutoStartMethod;

    public static string MethodText => CurrentMethod switch
    {
        "task" => "计划任务（登录自启，安装时创建）",
        "task+registry" => "计划任务 + 注册表双通道",
        "registry" => "注册表 Run 项",
        _ => "未启用",
    };

    public static bool IsEnabled => CurrentMethod.Length > 0;

    /// <summary>安装器创建的计划任务是否存在（任务缓存注册表只读探测，不调用外部命令）。</summary>
    public static bool InstallerTaskExists()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\" + TaskName);
            return key != null;
        }
        catch { return false; }
    }

    /// <summary>启用自启：注册表 Run（必成）+ 安装器任务存在则一并依赖。返回实际生效描述。</summary>
    public static string Enable()
    {
        Disable(clearSetting: false);
        using (var run = CreateRunKey())
        {
            run.SetValue(RunValueName, RunValue);
        }
        var task = InstallerTaskExists();
        SetMethod(task ? "task+registry" : "registry");
        WriteWatchdogVbs(); // 保留 vbs 供排障，不再注册计划任务（Defender 行为遏制）
        return task
            ? "已启用（安装时创建的计划任务 + 注册表双保险）"
            : "已启用注册表自启（被杀自动恢复由伴生进程承担）";
    }

    public static void Disable(bool clearSetting = true)
    {
        try
        {
            using var run = CreateRunKey();
            run.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { }
        if (clearSetting) SetMethod("");
    }

    private static RegistryKey CreateRunKey() =>
        Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
        ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

    public static void WriteWatchdogVbs(string? exe = null)
    {
        try
        {
            exe ??= ExePath;
            var dir = Path.GetDirectoryName(WatchdogVbsPath);
            if (dir != null) Directory.CreateDirectory(dir);
            // WScript 无控制台窗口：WMI 查询进程，不存在则静默拉起
            var lines = new[]
            {
                "' Classify watchdog",
                "Dim shell, wmi, procs",
                "Set shell = CreateObject(\"WScript.Shell\")",
                "Set wmi = GetObject(\"winmgmts:{impersonationLevel=impersonate}!\\\\.\\root\\cimv2\")",
                $"Set procs = wmi.ExecQuery(\"SELECT ProcessId FROM Win32_Process WHERE Name = '{Path.GetFileName(exe)}'\")",
                "If procs.Count = 0 Then",
                $"  shell.Run \"\"\"{exe}\"\" --autostart\", 0, False",
                "End If",
            };
            File.WriteAllText(WatchdogVbsPath, string.Join("\r\n", lines) + "\r\n");
        }
        catch (Exception ex)
        {
            App.Log.Error("写入看门狗脚本失败", ex);
        }
    }

    private static void SetMethod(string method)
    {
        SettingsService.Instance.Values.AutoStartMethod = method;
        SettingsService.Instance.Save();
    }
}
