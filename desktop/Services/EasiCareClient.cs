using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Classify.Services;

/// <summary>
/// EasiCareHelper 本机联动客户端（http://127.0.0.1:8900，仅回环，无鉴权）。
/// 早读看板数据源：名册 / 统计 / 行为流 / 打评价。服务未运行时优雅降级。
/// 评价失败（离线/令牌过期）时自动入缓存队列，恢复后一次性补写，不丢任何一条。
/// </summary>
public sealed class EasiCareClient
{
    public const string PerformanceReadAloud = "认真读书";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    public const string Base = "http://127.0.0.1:8900";

    public static bool LastAvailable { get; private set; }

    /// <summary>最近一次评价的结果摘要（供看板状态行 / Toast 展示）。</summary>
    public static string LastEvalStatus { get; private set; } = "";

    public async Task<bool> PingAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(Base + "/api/v1/students");
            LastAvailable = resp.IsSuccessStatusCode;
        }
        catch { LastAvailable = false; }
        return LastAvailable;
    }

    /// <summary>学生名册。EasiCare 不可用时返回空列表。</summary>
    public async Task<List<string>> GetStudentsAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(Base + "/api/v1/students");
            if (!resp.IsSuccessStatusCode) return new List<string>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("students", out var arr))
                foreach (var st in arr.EnumerateArray())
                    if (st.TryGetProperty("name", out var n)) list.Add(n.GetString() ?? "");
            LastAvailable = true;
            return list;
        }
        catch { LastAvailable = false; return new List<string>(); }
    }

    /// <summary>给学生打一次评价。失败自动入缓存队列（磁盘持久化），EasiCare 恢复后自动补写。</summary>
    public async Task<bool> EvalAsync(string studentName, string performanceName)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { studentName, performanceName }),
                Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(Base + "/api/v1/eval", content);
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                LastEvalStatus = "";
                return true;
            }
            var code = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("http", out var h) && h.GetInt32() == 401)
                    code = "401";
            }
            catch { }
            if (code == "401" || (int)resp.StatusCode == 401)
            {
                LastEvalStatus = "EasiCare 令牌已过期，请在 EasiCare 中做一次评价刷新";
                App.Log.Error("EasiCare 评价 401（令牌过期）→ 已入缓存队列", null);
            }
            else
            {
                LastEvalStatus = $"评价失败 HTTP {(int)resp.StatusCode}";
                App.Log.Aggregate($"EasiCare 评价失败({(int)resp.StatusCode})",
                    new HttpRequestException(body[..Math.Min(120, body.Length)]));
            }
            EnqueuePending(studentName, performanceName);
            return false;
        }
        catch (Exception ex)
        {
            LastEvalStatus = "连接 EasiCareHelper 失败";
            App.Log.Aggregate("EasiCare 调用异常", ex);
            EnqueuePending(studentName, performanceName);
            return false;
        }
    }

    /* ---------------- 评价缓存队列 ---------------- */

    public sealed class PendingEval
    {
        public string Student { get; set; } = "";
        public string Performance { get; set; } = "";
        public DateTime At { get; set; }
    }

    private static readonly List<PendingEval> PendingList = new();
    private static readonly object PendingGate = new();
    private static string PendingPath => System.IO.Path.Combine(AppPaths.DataDir, "easicare-pending.json");

    /// <summary>启动时从磁盘加载未写入的评价。</summary>
    public static void LoadPending()
    {
        try
        {
            PendingList.Clear();
            if (File.Exists(PendingPath))
            {
                var list = JsonSerializer.Deserialize<List<PendingEval>>(File.ReadAllText(PendingPath));
                if (list != null) PendingList.AddRange(list);
            }
        }
        catch { }
        if (PendingList.Count > 0)
            App.Log.Info($"EasiCare 评价缓存：{PendingList.Count} 条待写入");
    }

    private static void EnqueuePending(string student, string performance)
    {
        lock (PendingGate)
        {
            PendingList.Add(new PendingEval { Student = student, Performance = performance, At = DateTime.Now });
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PendingPath)!);
                File.WriteAllText(PendingPath,
                    JsonSerializer.Serialize(PendingList, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { App.Log.Error("保存评价缓存失败", ex); }
        }
    }

    /// <summary>当前待写入的评价条数。</summary>
    public static int PendingCount { get { lock (PendingGate) return PendingList.Count; } }

    /// <summary>尝试把所有缓存评价写入 EasiCare。返回成功写入的条数（0 = 仍不可用）。</summary>
    public static async Task<int> TryFlushPendingAsync()
    {
        List<PendingEval> snapshot;
        lock (PendingGate) snapshot = PendingList.ToList();
        if (snapshot.Count == 0) return 0;

        var client = new EasiCareClient();
        if (!await client.PingAsync()) return 0;

        var flushed = 0;
        foreach (var item in snapshot)
        {
            if (await client.EvalQuietAsync(item.Student, item.Performance))
            {
                lock (PendingGate)
                    PendingList.RemoveAll(p => p.Student == item.Student && p.Performance == item.Performance && p.At == item.At);
                flushed++;
                App.Log.Info($"评价缓存补写成功：{item.Student}（{item.Performance}）");
            }
            else break; // 第一条就失败说明还是不通，不用继续
        }
        if (flushed > 0)
        {
            lock (PendingGate)
            {
                try
                {
                    File.WriteAllText(PendingPath,
                        JsonSerializer.Serialize(PendingList, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
            }
        }
        return flushed;
    }

    /// <summary>静默评价（仅供 flush 内部用，失败不入队列）。</summary>
    private async Task<bool> EvalQuietAsync(string studentName, string performanceName)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { studentName, performanceName }),
                Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(Base + "/api/v1/eval", content);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
