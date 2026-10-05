using System.Text.Json;

namespace Classify.Services;

/// <summary>
/// 早读看板状态：小组 / 成员斜杠 / 小红花派生 / 任务 / 计时器。
/// 本地持久化 %AppData%\Classify\readboard.json；变更后快照上报服务端供 WebUI 读取。
/// 小红花规则：组斜杠每满 Ratio 条派生 1 朵，派生的评价按成员斜杠占比加权随机派给对应的人。
/// </summary>
public sealed class ReadBoardState
{
    public const int DefaultRatio = 3; // 每 3 条斜杠 = 1 朵小红花

    public int Ratio { get; set; } = DefaultRatio;
    public List<Group> Groups { get; set; } = new();

    /// <summary>任务单：可提前设置多个任务，开始后按任务时长倒计时。</summary>
    public List<TaskItem> Tasks { get; set; } = new();

    /// <summary>当前进行中的任务下标（-1 = 无）。</summary>
    public int ActiveTask { get; set; } = -1;

    public string CurrentTask { get; set; } = "";
    public int TaskMinutes { get; set; }
    public bool TimerRunning { get; set; }
    public DateTime? TimerStartedAt { get; set; }
    public int TimerPausedRemainingSec { get; set; }

    public sealed class TaskItem
    {
        public string Name { get; set; } = "";
        public int Minutes { get; set; }
    }

    public sealed class Group
    {
        public string Name { get; set; } = "";
        public List<Member> Members { get; set; } = new();
        public int FlowersAwarded { get; set; } // 已派发的小红花数
        public List<string> FlowerLog { get; set; } = new();
    }

    public sealed class Member
    {
        public string Name { get; set; } = "";
        public int Slashes { get; set; }
        public int Flowers { get; set; } // 该生实际获得的小红花（含点名直加与派生）
    }

    // ---------- 持久化 ----------
    private static string FilePath => Path.Combine(AppPaths.DataDir, "readboard.json");

    public static ReadBoardState Load()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path))
            {
                var st = JsonSerializer.Deserialize<ReadBoardState>(File.ReadAllText(path)) ?? new ReadBoardState();
                st.MigrateLegacy();
                return st;
            }
        }
        catch (Exception ex) { App.Log.Error("读板状态读取失败", ex); }
        return new ReadBoardState();
    }

    /// <summary>旧版单任务字段 → 任务单迁移。</summary>
    private void MigrateLegacy()
    {
        if (Tasks.Count == 0 && !string.IsNullOrWhiteSpace(CurrentTask))
        {
            Tasks.Add(new TaskItem { Name = CurrentTask, Minutes = TaskMinutes });
            if (ActiveTask < 0) ActiveTask = 0;
        }
    }

    /// <summary>当前任务（无则 null）。</summary>
    public TaskItem? Active() => ActiveTask >= 0 && ActiveTask < Tasks.Count ? Tasks[ActiveTask] : null;

    /// <summary>开始第 index 个任务：设置计时并记住任务名。</summary>
    public void StartTask(int index)
    {
        if (index < 0 || index >= Tasks.Count) return;
        ActiveTask = index;
        CurrentTask = Tasks[index].Name;
        TaskMinutes = Tasks[index].Minutes;
        TimerRunning = true;
        TimerStartedAt = DateTime.Now;
        TimerPausedRemainingSec = 0;
    }

    /// <summary>结束当前任务（保留在任务单里，可再次开始）。</summary>
    public void StopTask()
    {
        TimerRunning = false;
        TimerPausedRemainingSec = 0;
        TimerStartedAt = null;
    }

    /// <summary>按每组人数把全部成员重新均分（保留现有组名顺序，不够则新建「N组」）。</summary>
    public void AutoGroup(int sizePerGroup)
    {
        var members = Groups.SelectMany(g => g.Members).ToList();
        if (members.Count == 0) return;
        sizePerGroup = Math.Clamp(sizePerGroup, 2, 50);
        var names = Groups.Select(g => g.Name).ToList();
        Groups.Clear();
        var idx = 0;
        var gi = 1;
        while (idx < members.Count)
        {
            var name = gi <= names.Count ? names[gi - 1] : $"{gi}组";
            var g = new Group { Name = name };
            g.Members.AddRange(members.Skip(idx).Take(sizePerGroup));
            Groups.Add(g);
            idx += sizePerGroup;
            gi++;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { App.Log.Error("读板状态保存失败", ex); }
    }

    // ---------- 业务 ----------
    public Group? GetGroup(string name) =>
        Groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public Member? GetMember(Group g, string name) =>
        g.Members.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public int GroupSlashes(Group g) => g.Members.Sum(m => m.Slashes);

    /// <summary>组红花总数 = 各生实得之和（派生与点名直加都落在人头上）。</summary>
    public int GroupFlowers(Group g) => g.Members.Sum(m => m.Flowers);

    private static readonly EasiCareClient EasiCare = new();

    /// <summary>最近一次评价状态（透传给看板 Toast）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public static string LastEvalError => EasiCareClient.LastEvalStatus;

    /// <summary>
    /// 给小组随机 +1 斜杠（WebUI 组级按钮）：斜杠是可见板书计数不算评价；
    /// 斜杠模式下跨过阈值（每 Ratio 条 = 1 朵）才派生小红花并按占比加权随机到人、写评价。
    /// </summary>
    public async Task<(Member member, List<Member> flowerRecipients)> AddSlashAsync(Group g)
    {
        var m = PickWeighted(g);
        m.Slashes++;

        var recipients = await DistributeDerivedFlowersAsync(g);
        return (m, recipients);
    }

    /// <summary>按人 +1 斜杠（看板点具体学生）。</summary>
    public async Task<List<Member>> AddSlashToStudentAsync(Group g, Member m)
    {
        m.Slashes++;
        return await DistributeDerivedFlowersAsync(g);
    }

    /// <summary>斜杠模式：把新跨过阈值的派生小红花按占比加权随机派发并写评价。</summary>
    private async Task<List<Member>> DistributeDerivedFlowersAsync(Group g)
    {
        var recipients = new List<Member>();
        if (Ratio <= 0) return recipients;
        var flowersNow = GroupSlashes(g) / Ratio;
        while (g.FlowersAwarded < flowersNow)
        {
            g.FlowersAwarded++;
            var recipient = PickWeighted(g);
            recipient.Flowers++;
            g.FlowerLog.Add($"🌸 → {recipient.Name}");
            recipients.Add(recipient);
        }

        if (recipients.Count > 0 && SettingsService.Instance.Values.EasiCareEnabled && await EasiCare.PingAsync())
            foreach (var r in recipients)
                await EasiCare.EvalAsync(r.Name, EasiCareClient.PerformanceReadAloud);

        return recipients;
    }

    /// <summary>按人 +1 红花：直接给该生并当场写「认真读书」评价（两种模式通用）。</summary>
    public async Task<bool> AddFlowerToStudentAsync(Group g, Member m)
    {
        m.Flowers++;
        g.FlowersAwarded++;
        g.FlowerLog.Add($"🌸 → {m.Name}");
        if (SettingsService.Instance.Values.EasiCareEnabled && await EasiCare.PingAsync())
            return await EasiCare.EvalAsync(m.Name, EasiCareClient.PerformanceReadAloud);
        return false;
    }

    /// <summary>按人 −1 斜杠（本地回退；斜杠模式同步回收多出的派生花记账）。</summary>
    public void RemoveSlashFromStudent(Group g, Member m)
    {
        if (m.Slashes <= 0) return;
        m.Slashes--;
        if (Ratio > 0)
        {
            while (g.FlowersAwarded > GroupSlashes(g) / Ratio && g.FlowersAwarded > 0)
            {
                g.FlowersAwarded--;
                if (g.FlowerLog.Count > 0) g.FlowerLog.RemoveAt(g.FlowerLog.Count - 1);
            }
        }
    }

    /// <summary>按人 −1 红花（本地回退；EasiCare 评价无法撤回）。</summary>
    public void RemoveFlowerFromStudent(Group g, Member m)
    {
        if (m.Flowers <= 0) return;
        m.Flowers--;
        if (g.FlowersAwarded > 0) g.FlowersAwarded--;
        if (g.FlowerLog.Count > 0) g.FlowerLog.RemoveAt(g.FlowerLog.Count - 1);
    }

    /// <summary>
    /// 直接加花模式：+count 朵红花，每朵按占比加权随机派给一人并写「认真读书」评价。
    /// </summary>
    public async Task<List<Member>> AddFlowersAsync(Group g, int count)
    {
        var recipients = new List<Member>();
        for (int i = 0; i < Math.Max(1, count); i++)
        {
            var recipient = PickWeighted(g);
            g.FlowersAwarded++;
            recipient.Flowers++;
            g.FlowerLog.Add($"🌸 → {recipient.Name}");
            recipients.Add(recipient);
        }

        if (recipients.Count > 0 && SettingsService.Instance.Values.EasiCareEnabled && await EasiCare.PingAsync())
            foreach (var r in recipients)
                await EasiCare.EvalAsync(r.Name, EasiCareClient.PerformanceReadAloud);

        return recipients;
    }

    /// <summary>斜杠占比加权随机选人。</summary>
    private static Member PickWeighted(Group g)
    {
        var members = g.Members.Where(m => m.Slashes > 0).ToList();
        if (members.Count == 0) members = g.Members;
        if (members.Count == 0) throw new InvalidOperationException("小组没有成员");
        var total = members.Sum(m => Math.Max(1, m.Slashes));
        var roll = Random.Shared.Next(1, total + 1);
        foreach (var m in members)
        {
            roll -= Math.Max(1, m.Slashes);
            if (roll <= 0) return m;
        }
        return members[^1];
    }

    public void RemoveMember(Group g, string name)
    {
        var m = g.Members.FirstOrDefault(x => x.Name == name);
        if (m != null) g.Members.Remove(m);
    }

    public void AddGroup(string name)
    {
        if (!string.IsNullOrWhiteSpace(name) && GetGroup(name) is null)
            Groups.Add(new Group { Name = name.Trim() });
    }

    public void RemoveGroup(string name)
    {
        var g = GetGroup(name);
        if (g != null) Groups.Remove(g);
    }

    // ---------- 快照上报（WebUI 读取） ----------

    /// <summary>加分模式（slash/flower），由账号配置决定，看板窗口在加载配置后写回。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Mode { get; set; } = "slash";

    /// <summary>
    /// 隐私脱敏：学生姓名永不出教室电脑。云端快照只有「组内座号」（1 起的序号），
    /// WebUI 显示"组名·N号"，教师可抬头对照大屏（大屏每行显示序号+姓名）；
    /// 手机端按座号下发加减指令，由本机映射回真名写 EasiCare。
    /// FlowerLog（含姓名）不上传。任务单含任务名/时长（非学生信息）。
    /// </summary>
    public object Snapshot() => new
    {
        ratio = Ratio,
        mode = Mode,
        easicare = EasiCareClient.LastAvailable,
        tasks = Tasks.Select(t => new { name = t.Name, minutes = t.Minutes }),
        activeTask = ActiveTask,
        task = CurrentTask,
        taskMinutes = TaskMinutes,
        timerRunning = TimerRunning,
        timerRemainSec = TimerStartedAt is { } s && TimerRunning
            ? Math.Max(0, TaskMinutes * 60 - (int)(DateTimeOffset.UtcNow - s).TotalSeconds)
            : TimerPausedRemainingSec,
        groups = Groups.Select(g => new
        {
            name = g.Name,
            members = g.Members.Count,
            slashes = GroupSlashes(g),
            flowers = GroupFlowers(g),
            seats = g.Members.Select((m, i) => new { seat = i + 1, slashes = m.Slashes, flowers = m.Flowers }),
        }),
    };

    public async Task PublishAsync()
    {
        Save();
        try
        {
            await new ApiService().PublishReadBoardStateAsync(
                JsonSerializer.Serialize(Snapshot()));
        }
        catch (Exception ex) { App.Log.Aggregate("看板状态上报", ex); }
    }
}
