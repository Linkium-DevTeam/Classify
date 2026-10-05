using System.Diagnostics;
using System.Text.Json;
using Classify.UI;

namespace Classify.Services;

/// <summary>
/// 智能候课：课程表驱动。
/// 每分钟检查：当前时间进入「某节课开始前 N 分钟」窗口、本机不在全屏课件中、
/// 该节课教师（按学科匹配）配置了候课操作集 → 拉取并按序执行（每节课只触发一次）。
/// 高权限语义：操作集是受信任教师的授权动作序列，不受设备日常开关限制；
/// 教师手机端可"立即候课"/"标记本节学科"现场干预（消息 type=preclass）。
/// </summary>
public sealed class SmartPreclassService
{
    private static readonly ApiService Api = new();
    private static CancellationTokenSource? _cts;

    // 课程表缓存（10 分钟刷新）
    private static TimetableDoc? _tt;
    private static DateTime _ttFetched = DateTime.MinValue;
    private static readonly object Gate = new();

    private static string? _lastTriggeredKey; // "2026-10-01:3"

    // ---------- 数据模型 ----------
    public sealed class TimetableDoc
    {
        public int PreclassMinutes { get; set; } = 5;
        public List<Period> Periods { get; set; } = new();
        public List<Lesson> Lessons { get; set; } = new();
        public Dictionary<string, List<Lesson>> Overrides { get; set; } = new();
    }

    public sealed class Period { public int P { get; set; } public string Start { get; set; } = "08:00"; public string End { get; set; } = ""; }
    public sealed class Lesson { public int Day { get; set; } public int Period { get; set; } public string Subject { get; set; } = ""; }

    public sealed class ActionItem
    {
        public string Type { get; set; } = "";
        public string Param { get; set; } = "";
    }

    // ---------- 生命周期 ----------
    public static void Start()
    {
        if (!SettingsService.Instance.Values.WizardDone) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
        App.Log.Info("智能候课已启动");
    }

    public static void Stop() { _cts?.Cancel(); }

    private static async Task Loop(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { App.Log.Aggregate("智能候课", ex); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch { return; }
        }
    }

    private static async Task TickAsync(CancellationToken ct)
    {
        var s = SettingsService.Instance.Values;
        if (string.IsNullOrEmpty(s.DeviceToken)) return;

        // 课程表缓存 10 分钟
        if (_tt is null || (DateTime.UtcNow - _ttFetched).TotalMinutes > 10)
        {
            _tt = await Api.GetDeviceTimetableAsync();
            _ttFetched = DateTime.UtcNow;
            if (_tt is { Lessons.Count: > 0 })
                App.Log.Info($"课程表加载：{_tt.Lessons.Count} 节 · 候课提前 {_tt.PreclassMinutes} 分钟");
        }
        var tt = _tt;
        if (tt is null || tt.Lessons.Count == 0) return;

        var now = DateTime.Now;
        var todayKey = now.ToString("yyyy-MM-dd");
        var day = now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek;

        foreach (var lesson in tt.Lessons.Where(l => l.Day == day))
        {
            var subject = ResolveSubject(tt, todayKey, lesson.Period) ?? lesson.Subject;
            if (string.IsNullOrWhiteSpace(subject)) continue;

            var start = ParseStart(now, tt, lesson.Period);
            if (start is null) continue;
            var windowStart = start.Value.AddMinutes(-tt.PreclassMinutes);
            if (now < windowStart || now >= start.Value) continue; // 只在「开始前 N 分钟」窗口内

            var key = $"{todayKey}:{lesson.Period}";
            if (_lastTriggeredKey == key) return; // 本节课已触发

            // 学科 → 教师 → 操作集
            var teacher = await Api.GetSubjectTeacherAsync(subject);
            if (teacher is null)
            {
                _lastTriggeredKey = key;
                App.Log.Info($"候课：第 {lesson.Period} 节「{subject}」无对应学科教师，跳过");
                return;
            }
            var actions = await Api.GetActionSetByUidAsync(teacher.Uid);
            if (actions.Count == 0)
            {
                _lastTriggeredKey = key;
                App.Log.Info($"候课：{teacher.Username}（{subject}）未配置候课操作集，跳过");
                return;
            }

            _lastTriggeredKey = key;
            App.Log.Info($"候课触发：第 {lesson.Period} 节 {subject} · {teacher.Username}，执行 {actions.Count} 项动作");
            await ExecuteActionsAsync(actions, $"候课 · {subject} · {teacher.Username}", subject);
            return; // 一分钟内只执行一节
        }
    }

    public static string? ResolveSubject(TimetableDoc tt, string todayKey, int period)
    {
        if (tt.Overrides.TryGetValue(todayKey, out var list))
        {
            var o = list.FirstOrDefault(l => l.Period == period);
            if (o != null && !string.IsNullOrWhiteSpace(o.Subject)) return o.Subject;
        }
        return null;
    }

    public static DateTime? ParseStart(DateTime now, TimetableDoc tt, int period)
    {
        var p = tt.Periods.FirstOrDefault(x => x.P == period);
        if (p is null) return null;
        if (!TimeSpan.TryParse(p.Start, out var ts)) return null;
        return now.Date + ts;
    }

    /// <summary>课表里显式配置的结束时间（未配置返回 null，由调用方推导）。</summary>
    public static DateTime? ParseEnd(DateTime now, TimetableDoc tt, int period)
    {
        var p = tt.Periods.FirstOrDefault(x => x.P == period);
        if (p is null || string.IsNullOrWhiteSpace(p.End)) return null;
        if (!TimeSpan.TryParse(p.End, out var ts)) return null;
        return now.Date + ts;
    }

    // ---------- 执行器 ----------
    public static async Task ExecuteActionsAsync(List<ActionItem> actions, string context, string? subject = null)
    {
        // 候课场景自学习：同科目有学习记录时提示最常用应用（按频次排序，仅提示不自动执行）
        if (!string.IsNullOrWhiteSpace(subject))
        {
            var learned = LoadScenes().Where(s => s.Subject == subject)
                .OrderByDescending(s => s.LearnedAt).FirstOrDefault();
            if (learned is { Apps.Count: > 0 })
            {
                App.Log.Info($"候课自学习：{subject} 上次使用 {string.Join("、", learned.Apps)}");
                try
                {
                    ToastWindow.ShowToast("候课提醒",
                        $"上次{subject}课前你常用：{string.Join("、", learned.Apps.Take(3))}（可在操作集中固化为一键动作）");
                }
                catch { }
            }
        }

        foreach (var a in actions)
        {
            try
            {
                App.Log.Info($"候课动作 [{a.Type}] {a.Param}（{context}）");
                switch (a.Type)
                {
                    case "clock":
                        ClockWindow.StartClock();
                        if (int.TryParse(a.Param, out var mins) && mins > 0)
                            ClockWindow.Current?.SetCountdown(mins, context);
                        break;
                    case "tts":
                        if (!string.IsNullOrWhiteSpace(a.Param)) await TtsService.Instance.SpeakAsync(a.Param);
                        break;
                    case "open":
                        // 教师在云端配置的受信目标（文件/文件夹/网址），以系统默认程序打开
                        if (!string.IsNullOrWhiteSpace(a.Param))
                            Interop.Shell.Open(a.Param);
                        break;
                    case "volume":
                        if (double.TryParse(a.Param, out var pct)) Interop.Audio.SetMasterVolume(pct);
                        break;
                    case "board":
                        ReadBoardWindow.OpenBoard();
                        break;
                    case "delay":
                        if (int.TryParse(a.Param, out var sec)) await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(sec, 0, 300)));
                        break;
                }
            }
            catch (Exception ex)
            {
                App.Log.Error($"候课动作 {a.Type} 执行失败", ex);
            }
        }

        // 执行后开始 8 分钟前景采样，学习本课次常用应用（仅记录，供下次候课提示）
        if (!string.IsNullOrWhiteSpace(subject)) _ = LearnSceneAsync(subject);
    }

    // ---------- 候课场景自学习（按频次加权累计，跨次会话更聪明） ----------

    public sealed class Scene
    {
        public string Subject { get; set; } = "";
        public List<string> Apps { get; set; } = new();
        public DateTime LearnedAt { get; set; }
    }

    private static string ScenePath => Path.Combine(AppPaths.DataDir, "preclass-scenes.json");

    private static List<Scene> LoadScenes()
    {
        try
        {
            if (File.Exists(ScenePath))
                return JsonSerializer.Deserialize<List<Scene>>(File.ReadAllText(ScenePath)) ?? new();
        }
        catch { }
        return new();
    }

    private static void SaveScenes(List<Scene> scenes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath)!);
            File.WriteAllText(ScenePath, JsonSerializer.Serialize(scenes,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { App.Log.Error("保存候课场景失败", ex); }
    }

    private static bool IsIgnoredProcess(string proc) =>
        proc.Length == 0
        || proc.Equals("Classify", StringComparison.OrdinalIgnoreCase)
        || proc.Equals("explorer", StringComparison.OrdinalIgnoreCase)
        || proc.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
        || proc.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
        || proc.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)
        || proc.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
        || proc.Equals("TextServiceHost", StringComparison.OrdinalIgnoreCase)
        || proc.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
        || proc.Equals("ShellHost", StringComparison.OrdinalIgnoreCase);

    /// <summary>候课后 8 分钟内采样前台应用（每 3 秒），按科目累计频次——多次课后推荐越来越准。</summary>
    private static async Task LearnSceneAsync(string subject)
    {
        try
        {
            var scenes = LoadScenes();
            var scene = scenes.FirstOrDefault(s => s.Subject == subject);
            if (scene is null)
            {
                scene = new Scene { Subject = subject };
                scenes.Add(scene);
            }
            var sw = Stopwatch.StartNew();
            var counts = new Dictionary<string, int>();
            foreach (var a in scene.Apps)
            {
                var parts = a.Split('×');
                if (parts.Length == 2 && int.TryParse(parts[1], out var c))
                    counts[parts[0]] = c;
            }
            var last = "";
            while (sw.Elapsed.TotalMinutes < 8)
            {
                await Task.Delay(3000);
                var fg = ClassDetectService.Instance.ForegroundProcess;
                if (!string.IsNullOrWhiteSpace(fg) && fg != last && !IsIgnoredProcess(fg) && !fg.StartsWith("Windows", StringComparison.OrdinalIgnoreCase))
                {
                    counts[fg] = (counts.TryGetValue(fg, out var c) ? c : 0) + 1;
                }
                last = fg;
            }
            if (counts.Count == 0) return;
            scene.Apps = counts.OrderByDescending(kv => kv.Value).Take(8)
                .Select(kv => $"{kv.Key}×{kv.Value}").ToList();
            scene.LearnedAt = DateTime.Now;
            SaveScenes(scenes);
            App.Log.Info($"候课自学习：{subject} 记录 {string.Join("、", scene.Apps.Take(3))}");
        }
        catch (Exception ex) { App.Log.Error("候课自学习失败", ex); }
    }

    /// <summary>当前课程表快照（供看板问候行等读取）。</summary>
    public static TimetableDoc? GetTimetableSnapshot() => _tt;

    // ---------- 手机端手动干预 ----------
    public static async Task RunNowAsync()
    {
        var tt = _tt ?? await Api.GetDeviceTimetableAsync();
        if (tt is null || tt.Lessons.Count == 0)
        {
            ToastWindow.ShowToast("候课不可用", "设备管理员尚未上传课程表");
            return;
        }
        var now = DateTime.Now;
        var day = now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek;
        var todayKey = now.ToString("yyyy-MM-dd");

        // 当前节 = 已开始未结束（优先用课表显式结束时间，缺省按下一节开始/45 分钟推导）
        Lesson? current = null;
        foreach (var lesson in tt.Lessons.Where(l => l.Day == day).OrderBy(l => l.Period))
        {
            var start = ParseStart(now, tt, lesson.Period);
            if (start is null) continue;
            var explicitEnd = ParseEnd(now, tt, lesson.Period);
            var next = tt.Lessons.Where(l => l.Day == day && l.Period > lesson.Period)
                .OrderBy(l => l.Period).Select(l => ParseStart(now, tt, l.Period)).FirstOrDefault(x => x != null);
            var end = explicitEnd ?? next ?? start.Value.AddMinutes(45);
            if (now >= start.Value && now < end) { current = lesson; break; }
        }
        if (current is null) { ToastWindow.ShowToast("候课", "当前不在任何课程时段"); return; }

        var subject = ResolveSubject(tt, todayKey, current.Period) ?? current.Subject;
        await RunForSubjectAsync(subject, $"手动候课 · 第 {current.Period} 节");
    }

    /// <summary>托盘「候课（下一节学科）」：今天下一节的学科候课。</summary>
    public static async Task RunNextNowAsync()
    {
        var tt = _tt ?? await Api.GetDeviceTimetableAsync();
        if (tt is null || tt.Lessons.Count == 0)
        {
            ToastWindow.ShowToast("候课不可用", "设备管理员尚未上传课程表");
            return;
        }
        var now = DateTime.Now;
        var day = now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek;
        var todayKey = now.ToString("yyyy-MM-dd");

        Lesson? next = null;
        foreach (var lesson in tt.Lessons.Where(l => l.Day == day).OrderBy(l => l.Period))
        {
            var start = ParseStart(now, tt, lesson.Period);
            if (start is null || start.Value <= now) continue;
            next = lesson;
            break;
        }
        if (next is null)
        {
            // 今天没有更多课：取今天最后一节的学科
            var last = tt.Lessons.Where(l => l.Day == day).OrderByDescending(l => l.Period).FirstOrDefault();
            if (last is null) { ToastWindow.ShowToast("候课", "今天课表为空"); return; }
            var subject = ResolveSubject(tt, todayKey, last.Period) ?? last.Subject;
            await RunForSubjectAsync(subject, $"手动候课 · 今日末节 {subject}");
            return;
        }
        var subj = ResolveSubject(tt, todayKey, next.Period) ?? next.Subject;
        await RunForSubjectAsync(subj, $"手动候课 · 第 {next.Period} 节");
    }

    public static async Task RunForSubjectAsync(string subject, string context)
    {
        var teacher = await Api.GetSubjectTeacherAsync(subject);
        if (teacher is null) { ToastWindow.ShowToast("候课", $"没有学科为「{subject}」的教师"); return; }
        var actions = await Api.GetActionSetByUidAsync(teacher.Uid);
        if (actions.Count == 0) { ToastWindow.ShowToast("候课", $"{teacher.Username} 未配置候课操作集"); return; }
        await ExecuteActionsAsync(actions, context, subject);
        ToastWindow.ShowToast("候课已执行 ✅", context);
    }
}
