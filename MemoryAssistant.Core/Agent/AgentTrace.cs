namespace MemoryAssistant.Core.Agent;

/// <summary>一次工具调用的完整 trace（请求 + 结果配对后）。</summary>
public sealed record ToolCallTrace
{
    public string ToolCallId { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>参数 JSON（构建时截断 ~300 字）。</summary>
    public string ArgumentsJson { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
    /// <summary>工具输出摘要（构建时截断 ~200 字）。</summary>
    public string Output { get; init; } = "";
    public bool IsOmitted { get; init; }
    public double LatencyMs { get; init; }
    /// <summary>发起轮次（1-based LLM 调用序号）。</summary>
    public int Round { get; init; }
}

/// <summary>一轮 Agent 的 trace：LLM 调用 + 该轮工具执行。</summary>
public sealed record AgentRoundTrace
{
    /// <summary>LLM 调用序号（含 max_rounds 强制收尾轮）。</summary>
    public int Round { get; init; }
    /// <summary>整轮耗时（LLM + 工具执行）。</summary>
    public double LatencyMs { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public string? FinishReason { get; init; }
    /// <summary>本轮模型思考过程（reasoning_content；非推理模型为空）。</summary>
    public string? Reasoning { get; init; }
    /// <summary>
    /// 本轮 assistant 的正文。工具调用轮里这句话就是模型的"意图自述"
    /// （"我要先搜一下秋招"），是理解模型为什么这么做的关键，之前被整个丢掉。
    /// </summary>
    public string? AssistantContent { get; init; }
    public IReadOnlyList<ToolCallTrace> ToolCalls { get; init; } = [];
}
