using MemoryAssistant.Core.Missions;

namespace MemoryAssistant.Infrastructure.Missions;

/// <summary>
/// 任务编排写侧实现（V4.0 对话式编排）：草案确认 → 落库 → 持久化 → （按需）纳入调度。
/// </summary>
public sealed class MissionComposer : IMissionComposer
{
    private readonly MissionStore _store;
    private readonly MissionScheduler? _scheduler;
    private readonly Action _persist;

    public MissionComposer(MissionStore store, MissionScheduler? scheduler, Action persist)
    {
        _store = store;
        _scheduler = scheduler;
        _persist = persist;
    }

    public IReadOnlyList<MissionDefinition> Catalog => _store.Items;

    public MissionDefinition Create(MissionDraft draft, bool start = true)
    {
        var mission = _store.Add(draft.ToDefinition());
        // 这条路径只被"对话里确认草案"和 Agent 的工具创建调用 → 创建方式记为 Agent（§23）
        mission.Origin = MissionDefinition.OriginAgent;
        if (start && mission.Trigger != MissionTriggerKind.Manual)
            _scheduler?.StartMission(mission.Id);
        _persist();
        return mission;
    }

    public void Save() => _persist();
}
