using CommunityToolkit.Mvvm.ComponentModel;

namespace MemoryAssistant.App.Display;

/// <summary>
/// 一条 Agent 执行步骤的类型。刻意用"用户能看懂的阶段名"而不是源码里的类名：
/// 课程展示要让人一眼看出"Agent 是经过多个阶段完成任务的"，而不是"调了个模型"。
/// </summary>
public enum ExecutionStepKind
{
    /// <summary>理解用户意图（本轮开始）。</summary>
    Understanding,
    /// <summary>规划：决定要查什么、调哪些工具。</summary>
    Planning,
    /// <summary>执行某个能力（Skill）：如"统计聊天量与活跃度"、"回忆相关聊天"。</summary>
    Activity,
    /// <summary>写操作类工具调用（发送消息等）。</summary>
    Tool,
    /// <summary>读数据类工具调用（查会话/读记录/扫主题/取记忆）。</summary>
    Retrieval,
    /// <summary>组织答案。</summary>
    Answer,
    /// <summary>本轮结束。</summary>
    Complete,
}

/// <summary>
/// 一条执行步骤（对话右栏「执行过程」与「工作流」页共用的展示模型）。
///
/// 数据全部来自既有的 AgentProgress / ToolCallTrace / TaskTrace，**不新增任何后端语义**：
/// 这里只做"把已有轨迹映射成一条可读的步骤"这件事，两个页面共用同一份映射，
/// 避免各处再写一套（否则同一个工具调用在两页显示得不一样）。
/// </summary>
public sealed partial class ExecutionStep : ObservableObject
{
    public ExecutionStep(int number, ExecutionStepKind kind, string title)
    {
        Number = number;
        Kind = kind;
        Title = title;
    }

    /// <summary>步骤序号（1 起，与工作流节点/时间线对齐）。</summary>
    public int Number { get; }

    public ExecutionStepKind Kind { get; }

    /// <summary>阶段名（理解/规划/执行/工具/检索/作答/完成）。</summary>
    public string KindLabel => Kind switch
    {
        ExecutionStepKind.Understanding => "理解",
        ExecutionStepKind.Planning => "规划",
        ExecutionStepKind.Activity => "执行",
        ExecutionStepKind.Tool => "工具",
        ExecutionStepKind.Retrieval => "检索",
        ExecutionStepKind.Answer => "作答",
        _ => "完成",
    };

    /// <summary>步骤标题（"调用 search_messages"）。</summary>
    [ObservableProperty]
    private string _title;

    /// <summary>输入：工具参数摘要（"keyword=秋招, lookback_days=7"）。非工具步骤为空。</summary>
    [ObservableProperty]
    private string _args = "";

    /// <summary>输出：结果摘要（"23 条消息"）/ 阶段结论。空则不显示。</summary>
    [ObservableProperty]
    private string _result = "";

    /// <summary>pending（还没轮到）| running（进行中）| ok | failed。</summary>
    [ObservableProperty]
    private string _state = "running";

    /// <summary>耗时文本（"83ms"）；拿不到耗时留空，不编一个数出来。</summary>
    [ObservableProperty]
    private string _elapsed = "";

    public string StateGlyph => State switch
    {
        "ok" => "✓",
        "failed" => "✕",
        "running" => "●",
        _ => "○",
    };

    public bool HasArgs => Args.Length > 0;
    public bool HasResult => Result.Length > 0;
    public bool HasElapsed => Elapsed.Length > 0;

    partial void OnStateChanged(string value) => OnPropertyChanged(nameof(StateGlyph));
    partial void OnArgsChanged(string value) => OnPropertyChanged(nameof(HasArgs));
    partial void OnResultChanged(string value) => OnPropertyChanged(nameof(HasResult));
    partial void OnElapsedChanged(string value) => OnPropertyChanged(nameof(HasElapsed));

    /// <summary>把本步标成成功并记录耗时（"ms"）。</summary>
    public void Complete(long elapsedMs, string? result = null)
    {
        if (result is not null) Result = result;
        Elapsed = $"{elapsedMs}ms";
        State = "ok";
    }

    /// <summary>把本步标成失败（保留耗时，便于看"卡在哪一步"）。</summary>
    public void Fail(long elapsedMs, string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason)) Result = reason;
        Elapsed = $"{elapsedMs}ms";
        State = "failed";
    }
}

/// <summary>把工具名归类成"检索"还是"工具"（写操作）。规则简单且可解释，别在这里做语义猜测。</summary>
public static class ExecutionStepClassifier
{
    /// <summary>读数据类工具名里常见的词（命中即算"检索"，展示成 📚 那一步）。</summary>
    private static readonly string[] ReadHints =
    [
        "search", "find", "read", "scan", "retrieve", "recall",
        "memory", "rag", "timeline", "commitment", "profile", "topic", "stats", "list",
    ];

    public static ExecutionStepKind ClassifyTool(string toolName)
    {
        var name = toolName ?? "";
        foreach (var hint in ReadHints)
            if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return ExecutionStepKind.Retrieval;
        // 其余按"工具"处理：写操作（send_message 等）与未知工具，宁可显示成"工具"也不要误标成检索
        return ExecutionStepKind.Tool;
    }

    /// <summary>该工具名是否是写操作（展示成"需要确认"的一步）。</summary>
    public static bool IsWriteAction(string toolName)
        => (toolName ?? "").Contains("send", StringComparison.OrdinalIgnoreCase)
           || (toolName ?? "").Contains("reply", StringComparison.OrdinalIgnoreCase);
}
