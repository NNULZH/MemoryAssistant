using MemoryAssistant.Core.Agent.Trace;
using MemoryAssistant.Core.Missions;

namespace MemoryAssistant.Core.Agent;

/// <summary>一次对话消息（OpenAI 消息格式）。</summary>
public sealed record ChatMessage
{
    public string Role { get; init; } = ""; // system | user | assistant | tool
    public string? Content { get; init; }
    public string? ToolCallId { get; init; }
    public IReadOnlyList<ToolCallRequest>? ToolCalls { get; init; }
}

/// <summary>
/// 一次 Agent 运行的完整结果：最终答案 + 完整轨迹（供汇报/排查/降级）。
/// 语义：Rounds.Count == LLM 调用总次数（含 max_rounds 强制收尾轮，该轮 Round = MaxRounds+1）；
/// RoundCount 保留"规划轮数"语义（与旧行为一致）。
/// </summary>
public sealed record AgentResult
{
    public string Answer { get; init; } = "";
    public IReadOnlyList<ToolCallRequest> AllToolCalls { get; init; } = [];
    public int RoundCount { get; init; }
    public bool CompletedNormally { get; init; }
    public string? EarlyStopReason { get; init; }
    public double TotalElapsedMs { get; init; }
    /// <summary>逐轮 trace（每轮 LLM 调用 + 工具执行结果）。</summary>
    public IReadOnlyList<AgentRoundTrace> Rounds { get; init; } = [];
    public int TotalPromptTokens { get; init; }
    public int TotalCompletionTokens { get; init; }
    /// <summary>
    /// 全部轮次的思考过程拼接（reasoning_content 逐轮 + 工具轮的意图自述）。
    /// 只读派生属性：仅用于展示/排查，不回填模型。
    /// </summary>
    public string Reasoning => string.Join("\n\n", Rounds.Select(RoundThinking).Where(s => s.Length > 0));

    private static string RoundThinking(AgentRoundTrace r)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(r.Reasoning))
            sb.Append(r.Reasoning!.Trim());
        if (!string.IsNullOrWhiteSpace(r.AssistantContent))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("（说）").Append(r.AssistantContent!.Trim());
        }
        return sb.ToString();
    }

    /// <summary>最终被引用的证据（EvidenceStore 快照，供会话记忆/UI 证据弹窗复用；P16+）。</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
    /// <summary>任务级 Trace（Orchestrator 路径；P8 的 Rounds 属于旧 AgentLoop 路径，互不冲突）。</summary>
    public TaskTrace? TaskTrace { get; init; }
    /// <summary>
    /// 任务草案（对话式编排）：非空表示本轮用户是在"派活"，UI 应展示任务确认卡，
    /// 用户点确认后才真正创建（写入 + 调度）。
    /// </summary>
    public MissionDraft? PendingMission { get; init; }
}

/// <summary>Agent 运行的上下文：保留多轮历史，供 REPL 与 Workflow 复用。</summary>
public sealed class AgentContext
{
    private readonly List<ChatMessage> _messages = [];
    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void Add(ChatMessage msg) => _messages.Add(msg);
    public void Clear() => _messages.Clear();
}

/// <summary>进度事件类型。</summary>
public enum AgentProgressKind
{
    /// <summary>状态描述（"正在用「tools」查…"）——整条替换，不是增量。</summary>
    Status = 0,
    /// <summary>模型思考的增量文本（reasoning_content 边生成边推）。</summary>
    ThinkingDelta = 1,
    /// <summary>最终回答的增量文本。</summary>
    AnswerDelta = 2,
    /// <summary>工具调用（同一次调用推两条：Phase=start 表示"发出去了"，Phase=finish 表示"拿到结果"）。</summary>
    ToolCall = 3,
}

/// <summary>
/// Agent 运行过程中的进度（流式反馈给 UI）。
/// 时序上：思考（ThinkingDelta）先流完，答案（AnswerDelta）才开始流——
/// 用户先看到"模型在想什么"，再看到成文的回答。
///
/// 工具调用（ToolCall）单独成一条事件流：让用户和排查者都能实时看到
/// "这一步到底调了什么工具、给了什么参数、拿到了什么"——
/// 光看思考文字无法区分"模型真的查了"和"模型只是在说它要去查"（实测踩过这个坑）。
/// </summary>
public sealed record AgentProgress
{
    public AgentProgressKind Kind { get; init; } = AgentProgressKind.Status;
    public string Text { get; init; } = "";

    /// <summary>ToolCall 专用：start（已发出调用）/ finish（已拿到结果）。</summary>
    public string Phase { get; init; } = "";
    /// <summary>ToolCall 专用：工具名（或写操作的动作名）。</summary>
    public string ToolName { get; init; } = "";
    /// <summary>ToolCall 专用：参数摘要（"keyword=张晓明, private_only=true"）。</summary>
    public string ToolArgs { get; init; } = "";
    /// <summary>ToolCall/finish 专用：结果摘要（成功给内容摘要，失败给错误原因）。</summary>
    public string ToolSummary { get; init; } = "";
    /// <summary>ToolCall/finish 专用：这次调用成功没有。</summary>
    public bool ToolSuccess { get; init; }

    public static AgentProgress Status(string text) => new() { Kind = AgentProgressKind.Status, Text = text };
    public static AgentProgress Thinking(string delta) => new() { Kind = AgentProgressKind.ThinkingDelta, Text = delta };
    public static AgentProgress Answer(string delta) => new() { Kind = AgentProgressKind.AnswerDelta, Text = delta };

    /// <summary>工具开始执行。argsDisplay 用 <see cref="CompactArguments"/> 压过，或写操作直接给 "person=张三"。</summary>
    public static AgentProgress ToolStart(string name, string argsDisplay) => new()
    {
        Kind = AgentProgressKind.ToolCall,
        Phase = "start",
        ToolName = name,
        ToolArgs = Clamp(argsDisplay, 200),
        Text = $"调用 {name}({Clamp(argsDisplay, 200)})",
    };

    /// <summary>工具执行结束（成功给结果摘要，失败给错误原因）。</summary>
    public static AgentProgress ToolEnd(string name, bool success, string? summary) => new()
    {
        Kind = AgentProgressKind.ToolCall,
        Phase = "finish",
        ToolName = name,
        ToolSuccess = success,
        ToolSummary = Clamp(Flatten(summary), 200),
        Text = success ? $"{name} 完成" : $"{name} 失败",
    };

    /// <summary>
    /// 把工具参数的 JSON 压成一行"k=v, k=v"——直接显示原始 JSON 又长又难扫，
    /// 而"模型给了哪些参数"恰恰是排查"它是不是查错人了"的关键，不能省。
    /// </summary>
    public static string CompactArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return Clamp(json, 200);

            var parts = new List<string>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => p.Value.GetString() ?? "",
                    System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => "",
                    // 注意：JsonElement.ToString() 对布尔会给出 "True/False"（C# 写法），
                    // 而模型发的是 JSON 的 true/false——照原样显示，别让参数看起来被改过
                    System.Text.Json.JsonValueKind.True => "true",
                    System.Text.Json.JsonValueKind.False => "false",
                    System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array => p.Value.GetRawText(),
                    _ => p.Value.ToString(),
                };
                parts.Add($"{p.Name}={v}");
            }
            return Clamp(string.Join(", ", parts), 200);
        }
        catch (System.Text.Json.JsonException)
        {
            return Clamp(json, 200);   // 不是 JSON 就原样显示，别把信息丢了
        }
    }

    /// <summary>多行文本压成一行（工具输出常是多行 JSON/文本，摘要只需要第一眼的结论）。</summary>
    private static string Flatten(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal));
        return string.Join(" · ", lines);
    }

    private static string Clamp(string? s, int max)
    {
        var t = (s ?? "").Replace('\r', ' ').Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}
