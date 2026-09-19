namespace MemoryAssistant.Core.Agent;

/// <summary>一次 LLM 响应：普通文本 或 工具调用请求。</summary>
public sealed record ChatResult
{
    public string? Content { get; init; }
    /// <summary>
    /// 推理模型的思考过程（DeepSeek reasoner 的 reasoning_content）。仅用于展示/排查，
    /// **绝不能回填进下一次请求的 messages**（DeepSeek 会直接报 400）。
    /// </summary>
    public string? ReasoningContent { get; init; }
    public IReadOnlyList<ToolCallRequest>? ToolCalls { get; init; }
    public bool HasToolCalls => ToolCalls is { Count: > 0 };
    public string? FinishReason { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
}

/// <summary>LLM 客户端抽象。实现由 Infrastructure 或 Features 提供（DeepSeek/OpenAI 兼容）。</summary>
public interface IChatClient
{
    /// <summary>发送完整对话（含历史），返回模型响应。必须支持 Function Calling。</summary>
    Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSchema>? tools = null,
        CancellationToken ct = default);

    string ModelName { get; }
}

/// <summary>
/// 可选的流式能力：正文/思考边生成边回调（UI 不必等整段生成完）。
/// 与 <see cref="IChatClient"/> 分开是为了不破坏既有实现（含单测里的假客户端）：
/// 不支持流式的实现照旧走 <see cref="IChatClient.ChatAsync"/>。
/// 返回的仍是**完整结果**（含 tool_calls），调用方只把回调当成"增量预览"，逻辑与一次性调用完全一致。
/// </summary>
public interface IStreamingChatClient
{
    Task<ChatResult> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSchema>? tools,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta,
        CancellationToken ct = default);
}

/// <summary>
/// 可选的"强制工具调用"能力（OpenAI/DeepSeek 的 <c>tool_choice</c> 指定具体函数）。
/// 与 <see cref="IChatClient"/> 分开，理由同上：不支持的实现照旧走 ChatAsync，既有代码零改动。
///
/// 用途（第三阶段补充）：用户明确说"给某人发消息 / 回复他"时，**必须真的发起一次对应工具调用**；
/// 但模型有时只生成一段"我这就发…"的自然语言就算完事——那等于假装执行了。
/// 这时用它把 tool_choice 钉到具体函数上，把"该调的工具"变成协议层强制要求。
/// </summary>
public interface IToolChoiceChatClient
{
    /// <summary>要求模型本轮必须调用 <paramref name="forcedToolName"/>（其余工具仍可见，便于它先补充信息）。</summary>
    Task<ChatResult> ChatWithForcedToolAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSchema> tools,
        string forcedToolName,
        Action<string>? reasoningDelta = null,
        CancellationToken ct = default);
}
