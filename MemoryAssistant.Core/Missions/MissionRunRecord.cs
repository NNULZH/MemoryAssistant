namespace MemoryAssistant.Core.Missions;

/// <summary>
/// 一次任务执行的**长期记忆**（第三阶段补充：任务记忆可查看/可删除）。
///
/// 为什么要单独一条记录，而不是只留任务上的 <see cref="MissionDefinition.LastResult"/>：
///   · 任务上只能留"最近一次"，用户问"上周三那次到底跑了什么"就查不到了；
///   · 调度器日志列表是**内存态**（重启即空）且把结果截断到 60 字，只能当"滚动日志"看；
///   · 这条记录**不截断正文**，并按任务持久化到磁盘——它就是"任务干过什么"的账本。
/// </summary>
public sealed record MissionRunRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid MissionId { get; init; }
    /// <summary>执行当时的任务名（任务被改名/删除后，账本还能读得懂）。</summary>
    public string MissionTitle { get; init; } = "";
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public bool Success { get; init; } = true;
    /// <summary>因"没有新内容"跳过（追踪型任务），不是失败。</summary>
    public bool Skipped { get; init; }
    /// <summary>被用户终止（任务页「终止执行」）——既不是失败也不是完成，如实单列。</summary>
    public bool Cancelled { get; init; }
    /// <summary>完整结果正文（**不截断**——"可查看"就指这条）。</summary>
    public string Summary { get; init; } = "";
    public int EvidenceCount { get; init; }
    public double ElapsedMs { get; init; }
    /// <summary>执行轨迹（展开用），可为空。</summary>
    public string TraceText { get; init; } = "";

    // ---- 展示用（UI 直接绑，避免在 XAML 里写逻辑）----
    public string AtText => At.ToString("MM-dd HH:mm:ss");
    public string StatusText => Cancelled ? "已终止"
        : Skipped ? "跳过（无新增）"
        : Success ? "完成" : "失败";
    public string MetaText => $"{StatusText} · {ElapsedMs:0}ms"
                              + (EvidenceCount > 0 ? $" · {EvidenceCount} 条证据" : "")
                              + (TraceText.Length > 0 ? " · 有轨迹" : "");
    /// <summary>列表里的一行摘要（短；点开看全文）。</summary>
    public string Preview => Summary.Length <= 80 ? Summary : Summary[..80] + "…";
    public bool HasTrace => TraceText.Length > 0;

    public override string ToString() => $"[{AtText}] {StatusText}｜{Preview}";
}

/// <summary>
/// 任务执行记录（长期记忆）的存储口：可追加、可查看、可删除单条、可清空。
/// Core 只定义契约；落盘实现放在 Infrastructure（data/mission-runs.json）。
/// </summary>
public interface IMissionRunStore
{
    /// <summary>追加一条执行记录（实现应落盘；写失败不得影响任务执行本身）。</summary>
    void Append(MissionRunRecord record);

    /// <summary>按时间**倒序**列出记录；给定 missionId 则只看该任务的。</summary>
    IReadOnlyList<MissionRunRecord> List(Guid? missionId = null, int limit = 200);

    /// <summary>删除单条记录（用户手动清理用）。</summary>
    bool Delete(Guid recordId);

    /// <summary>清空记录（给定 missionId 则只清该任务的）。返回删除条数。</summary>
    int Clear(Guid? missionId = null);

    /// <summary>当前记录的落盘位置（便于日志/界面上告诉用户去哪儿看）。空 = 未落盘。</summary>
    string Location => "";
}

/// <summary>内存实现：单测与"没接落盘"的场景用（重启即空）。</summary>
public sealed class InMemoryMissionRunStore : IMissionRunStore
{
    private readonly List<MissionRunRecord> _items = [];
    private readonly object _gate = new();

    public void Append(MissionRunRecord record)
    {
        lock (_gate) _items.Add(record);
    }

    public IReadOnlyList<MissionRunRecord> List(Guid? missionId = null, int limit = 200)
    {
        lock (_gate)
            return _items
                .Where(r => missionId is null || r.MissionId == missionId)
                .OrderByDescending(r => r.At)
                .Take(Math.Max(1, limit))
                .ToList();
    }

    public bool Delete(Guid recordId)
    {
        lock (_gate) return _items.RemoveAll(r => r.Id == recordId) > 0;
    }

    public int Clear(Guid? missionId = null)
    {
        lock (_gate) return _items.RemoveAll(r => missionId is null || r.MissionId == missionId);
    }
}
