using System.Text;

namespace Classify.Services;

/// <summary>极简文件日志：%AppData%\Classify\logs（按天分文件，聚合降噪）。</summary>
public sealed class LogService
{
    private static readonly object Gate = new();
    private string _dir = "";

    public void Init()
    {
        try
        {
            _dir = Path.Combine(AppPaths.DataDir, "logs");
            Directory.CreateDirectory(_dir);
            Info($"==== 会话开始 {AppVersion} ====");
        }
        catch { /* 日志失败不影响运行 */ }
    }

    public static string AppVersion =>
        typeof(LogService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string LogDirectory => _dir;

    public void Info(string msg) => Write("INFO", msg, null);
    public void Error(string msg, Exception? ex = null) => Write("ERRO", msg, ex);

    private readonly object _aggGate = new();
    private readonly Dictionary<string, (int Count, DateTime WindowStart)> _agg = new();

    /// <summary>
    /// 聚合式错误日志（降噪）：同类错误首次记完整堆栈，一小时内静默计数，
    /// 满一小时写一条汇总。用于轮询等高频可自愈错误，避免日志刷屏。
    /// </summary>
    public void Aggregate(string category, Exception? ex)
    {
        if (_dir.Length == 0) return;
        lock (_aggGate)
        {
            var now = DateTime.Now;
            if (!_agg.TryGetValue(category, out var e))
            {
                _agg[category] = (1, now);
                Write("ERRO", $"{category}（首次）: {ex?.Message}", ex);
                return;
            }
            _agg[category] = (e.Count + 1, e.WindowStart);
            if ((now - e.WindowStart).TotalMinutes >= 60)
            {
                Write("INFO", $"{category} 近一小时聚合 ×{e.Count + 1}（已降噪）", null);
                _agg[category] = (0, now);
            }
        }
    }

    private void Write(string level, string msg, Exception? ex)
    {
        if (_dir.Length == 0) return;
        try
        {
            lock (Gate)
            {
                var file = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ')
                  .Append(level).Append(' ').AppendLine(msg);
                if (ex != null) sb.AppendLine(ex.ToString());
                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { }
    }

    private static readonly char[] LineBreak = { '\n' };

    /// <summary>
    /// 反馈附日志用的脱敏尾部：截取最近 maxChars 字符，并把令牌/密钥/硬件指纹等敏感值替换为 ***。
    /// 日志中不会出现学生姓名（看板姓名本就不写日志），此处只需清除凭据类内容。
    /// </summary>
    public string RedactedTail(int maxChars = 6000)
    {
        try
        {
            var files = Directory.Exists(_dir)
                ? Directory.GetFiles(_dir, "app-*.log").OrderBy(f => f).ToArray()
                : Array.Empty<string>();
            if (files.Length == 0) return "";
            var sb = new StringBuilder();
            foreach (var f in files[^2..]) // 最近两天足够定位问题
            {
                sb.Append(File.ReadAllText(f, Encoding.UTF8));
                sb.Append('\n');
            }
            var text = sb.ToString();
            if (text.Length > maxChars) text = "…（仅保留末段）\n" + text[^maxChars..];

            // 凭据脱敏：settings 中的敏感值 + 通用 JWT/长密钥形态
            var secrets = new List<string>();
            try
            {
                var s = SettingsService.Instance.Values;
                if (!string.IsNullOrEmpty(s.DeviceToken)) secrets.Add(s.DeviceToken);
                if (!string.IsNullOrEmpty(s.AzureKey)) secrets.Add(s.AzureKey);
                if (!string.IsNullOrEmpty(s.HwId)) secrets.Add(s.HwId);
            }
            catch { }
            foreach (var sec in secrets)
                if (sec.Length > 8) text = text.Replace(sec, "***");
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", "***");
            return text;
        }
        catch { return ""; }
    }
}
