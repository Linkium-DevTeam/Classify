namespace Classify.Services;

/// <summary>
/// 备用域名自动切换：主域连续网络失败 ≥3 次自动切换到备用域（workers.dev），
/// 任一域名请求成功即重置其失败计数。设置中的 ServerUrl 仍是"账号身份"，
/// 切换只影响当前会话请求走哪个域名。
/// </summary>
public static class ServerEndpoints
{
    private static readonly string[] Bases =
    {
        "https://ccall.linkium.top",
        "https://classcall.linkium.workers.dev",
    };

    private static readonly object Gate = new();
    private static int _current;
    private static readonly Dictionary<string, int> Fails = new();
    private static bool _inited;

    public static void InitFrom(string? settingsUrl)
    {
        lock (Gate)
        {
            if (_inited) return;
            _inited = true;
            if (string.IsNullOrWhiteSpace(settingsUrl)) return;
            var u = settingsUrl.Trim().TrimEnd('/');
            var idx = Array.FindIndex(Bases, b => b.Equals(u, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) { _current = idx; return; }
            // 自定义部署：主位置替换为用户域名，workers.dev 仍作备用
            Bases[0] = u;
            _current = 0;
        }
    }

    public static string Current
    {
        get { lock (Gate) { return Bases[_current]; } }
    }

    public static void ReportFailure(string baseUrl)
    {
        lock (Gate)
        {
            Fails[baseUrl] = Fails.GetValueOrDefault(baseUrl) + 1;
            if (Fails[baseUrl] < 3 || Bases.Length < 2 || Bases[_current] != baseUrl) return;
            _current = (_current + 1) % Bases.Length;
            App.Log.Info($"网络连续失败，已切换备用服务器：{Bases[_current]}");
        }
    }

    public static void ReportSuccess(string baseUrl)
    {
        lock (Gate) { Fails[baseUrl] = 0; }
    }
}
