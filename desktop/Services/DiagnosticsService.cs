using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Classify.Services;

/// <summary>
/// 超早期启动诊断：在任何服务初始化之前记录启动阶段与致命异常。
///
/// 设计约束：
/// - 只写本机 %LOCALAPPDATA%\Classify\boot-log.txt，日志系统本身故障也能留痕；
/// - 上传严格跟随“匿名遥测”总开关（默认开启，用户可关闭），未完成向导前不上传；
/// - 上传前脱敏：用户名、用户目录、设备令牌、Azure 密钥、硬件指纹；
/// - 节流上传（60 秒一次），失败静默——诊断通道永不影响主流程；
/// - 致命异常走同步上传（最多 6 秒），随后进程退出。
/// </summary>
public static class DiagnosticsService
{
    private const int MaxMemoryLines = 300;
    private const int MaxUploadChars = 15_000;
    private static readonly List<string> Lines = new();
    private static readonly object Gate = new();
    private static DateTime _lastUpload = DateTime.MinValue;
    private static bool _settingsReady;

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Classify", "boot-log.txt");

    public static void Boot(string message)
    {
        var line = DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + " " + message;
        lock (Gate)
        {
            Lines.Add(line);
            if (Lines.Count > MaxMemoryLines)
                Lines.RemoveRange(0, Lines.Count - MaxMemoryLines);
        }
        AppendToDisk(line);
        if (_settingsReady) QueueUpload(force: false);
    }

    /// <summary>设置加载完成后调用：把启动早期积累的日志补传一次（仍受遥测开关约束）。</summary>
    public static void OnSettingsLoaded()
    {
        _settingsReady = true;
        QueueUpload(force: true);
    }

    /// <summary>致命异常：阻塞上传当前快照，确保现场送达后再退出。</summary>
    public static void FlushSync()
    {
        if (!UploadAllowed()) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var body = new StringContent(Snapshot(), Encoding.UTF8, "text/plain");
            http.PostAsync(ServerEndpoints.Current + "/api/diag/boot", body).GetAwaiter().GetResult();
        }
        catch { /* 诊断失败不影响退出 */ }
    }

    private static void QueueUpload(bool force)
    {
        if (!UploadAllowed()) return;
        lock (Gate)
        {
            var now = DateTime.Now;
            if (!force && (now - _lastUpload).TotalSeconds < 60) return;
            _lastUpload = now;
        }
        var snapshot = Snapshot();
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                using var body = new StringContent(snapshot, Encoding.UTF8, "text/plain");
                await http.PostAsync(ServerEndpoints.Current + "/api/diag/boot", body);
            }
            catch { }
        });
    }

    private static bool UploadAllowed()
    {
        try
        {
            return _settingsReady
                && SettingsService.Instance.Values.WizardDone
                && SettingsService.Instance.Values.TelemetryEnabled
                && !string.IsNullOrWhiteSpace(ServerEndpoints.Current);
        }
        catch { return false; }
    }

    private static string Snapshot()
    {
        string raw;
        lock (Gate) raw = string.Join(Environment.NewLine, Lines);
        if (raw.Length > MaxUploadChars) raw = raw[^MaxUploadChars..];
        return Redact(raw);
    }

    private static void AppendToDisk(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // 简单轮转：超过 512KB 时保留后半段，避免无限增长
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 512 * 1024)
            {
                var text = File.ReadAllText(LogPath);
                File.WriteAllText(LogPath, text[^(256 * 1024)..]);
            }
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>脱敏：用户目录、用户名、设备令牌、Azure 密钥、硬件指纹。</summary>
    private static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        try
        {
            var user = Environment.UserName;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                text = text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(user) && user.Length >= 2)
                text = Regex.Replace(text, Regex.Escape(user), "%USER%", RegexOptions.IgnoreCase);

            var s = SettingsService.Instance?.Values;
            if (s != null)
            {
                if (!string.IsNullOrEmpty(s.DeviceToken)) text = text.Replace(s.DeviceToken, "***");
                if (!string.IsNullOrEmpty(s.AzureKey)) text = text.Replace(s.AzureKey, "***");
                if (!string.IsNullOrEmpty(s.HwId)) text = text.Replace(s.HwId, "***");
            }
        }
        catch { }
        return text;
    }
}
