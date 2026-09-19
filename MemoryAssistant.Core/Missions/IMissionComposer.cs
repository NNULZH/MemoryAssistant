namespace MemoryAssistant.Core.Missions;

/// <summary>
/// 任务编排的"写侧"（对话式编排的最后一步）：
/// UI 拿到 <see cref="MissionDraft"/> 并经用户确认后，调用这里把草案真正落库（并按需纳入调度）。
/// 抽象放 Core、实现放 Infrastructure，保证"理解需求"（零副作用）与"真的创建"（有副作用）分离。
/// </summary>
public interface IMissionComposer
{
    /// <summary>当前任务目录（UI 展示用）。</summary>
    IReadOnlyList<MissionDefinition> Catalog { get; }

    /// <summary>创建任务：写入目录 + 持久化；start=true 且非手动任务时立即纳入调度。</summary>
    MissionDefinition Create(MissionDraft draft, bool start = true);

    /// <summary>持久化当前目录（外部改动后调用）。</summary>
    void Save();
}
