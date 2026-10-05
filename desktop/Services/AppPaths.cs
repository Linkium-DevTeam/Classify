namespace Classify.Services;

/// <summary>应用数据目录：%AppData%\Classify。首次运行自动整体迁移旧版 %AppData%\ClassCallLite。</summary>
public static class AppPaths
{
    public static string DataDir { get; }

    static AppPaths()
    {
        // 测试/多实例隔离：环境变量可重定向数据目录（正常使用不受影响）
        var envDir = Environment.GetEnvironmentVariable("CLASSIFY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            Directory.CreateDirectory(envDir);
            DataDir = envDir;
            return;
        }

        var user = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var newDir = Path.Combine(user, "Classify");
        var oldDir = Path.Combine(user, "ClassCallLite");

        try
        {
            if (Directory.Exists(oldDir) && !File.Exists(Path.Combine(newDir, "settings.json")))
            {
                Directory.Move(oldDir, newDir);
            }
        }
        catch { /* 迁移失败（占用/权限）时新目录照常工作，旧数据保留 */ }

        Directory.CreateDirectory(newDir);
        DataDir = newDir;
    }
}
