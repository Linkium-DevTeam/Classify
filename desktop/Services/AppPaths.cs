namespace Classify.Services;

/// <summary>
/// 应用数据目录：优先 %AppData%\Classify；首次运行自动迁移旧版 %AppData%\ClassCallLite。
/// 对漫游配置损坏、只读重定向、企业策略限制做多级回退，避免启动期直接崩溃。
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; }

    static AppPaths()
    {
        // 测试/多实例隔离：环境变量可重定向数据目录（正常使用不受影响）
        var envDir = Environment.GetEnvironmentVariable("CLASSIFY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envDir) && TryPrepare(envDir, out var envPrepared))
        {
            DataDir = envPrepared;
            return;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var newDir = string.IsNullOrWhiteSpace(appData) ? "" : Path.Combine(appData, "Classify");
        var oldDir = string.IsNullOrWhiteSpace(appData) ? "" : Path.Combine(appData, "ClassCallLite");

        if (!string.IsNullOrWhiteSpace(newDir))
        {
            try
            {
                if (Directory.Exists(oldDir) && !File.Exists(Path.Combine(newDir, "settings.json")))
                    Directory.Move(oldDir, newDir);
            }
            catch { /* 迁移失败（占用/权限）时新目录照常工作，旧数据保留 */ }
            if (TryPrepare(newDir, out var appDataPrepared))
            {
                DataDir = appDataPrepared;
                return;
            }
        }

        // 漫游目录不可写时回退到 LocalAppData（学校常见：漫游配置服务器不可达）
        if (!string.IsNullOrWhiteSpace(localData) &&
            TryPrepare(Path.Combine(localData, "Classify"), out var localPrepared))
        {
            DataDir = localPrepared;
            return;
        }

        // 最后兜底：临时目录（功能可降级，但应用必须能启动）
        var fallback = Path.Combine(Path.GetTempPath(), "Classify");
        try { Directory.CreateDirectory(fallback); } catch { }
        DataDir = fallback;
    }

    private static bool TryPrepare(string path, out string prepared)
    {
        prepared = "";
        try
        {
            Directory.CreateDirectory(path);
            // 真正验证可写，而不只是可创建（某些重定向目录可创建但不可写）
            var probe = Path.Combine(path, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            prepared = path;
            return true;
        }
        catch { return false; }
    }
}
