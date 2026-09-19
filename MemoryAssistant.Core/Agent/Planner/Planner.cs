using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Core.Agent.Planner;

/// <summary>
/// 默认规划器：LLM 结构化计划 → PlanValidator 校验 → 失败回退 RulePlanner。
/// 始终返回"可用计划"（校验通过 或 规则计划），不给上层抛不可恢复的计划错误。
/// chat 为 null 或 preferLlm=false 时直接走规则（零 token）。
/// </summary>
public sealed class Planner : IPlanner
{
    private readonly IChatClient? _chat;
    private readonly PlanValidator _validator;
    private readonly IAppLogger? _logger;
    private readonly int _maxSteps;

    public Planner(SkillCatalog catalog, AgentOptions options, IChatClient? chat = null, IAppLogger? logger = null)
    {
        Catalog = catalog;
        _chat = chat;
        _validator = new PlanValidator(catalog, options);
        _logger = logger;
        _maxSteps = options.MaxPlanSteps > 0 ? options.MaxPlanSteps : 8;
    }

    public SkillCatalog Catalog { get; }

    public async Task<AgentPlan> PlanAsync(
        string query,
        PlannerHint? hint = null,
        bool preferLlm = true,
        CancellationToken ct = default,
        Action<string>? onReasoningDelta = null)
    {
        var fallback = RulePlanner.RulePlan(query, hint);

        if (!preferLlm || _chat is null)
            return fallback;

        try
        {
            var (parsed, reasoning) = await TryLlmPlanAsync(query, ct, onReasoningDelta);
            if (parsed is not null)
            {
                var llmPlan = parsed with { FromLlm = true, Reasoning = reasoning };
                var check = _validator.Validate(llmPlan);
                if (check.Ok)
                {
                    _logger?.Debug($"[Planner] LLM 计划通过校验：{check.Repaired!.Steps.Count} 步");
                    return check.Repaired!;
                }
                _logger?.Warn($"[Planner] LLM 计划非法（{string.Join("; ", check.Errors)}），回退规则计划。");
            }
            else
            {
                _logger?.Warn("[Planner] LLM 未返回可解析计划，回退规则计划。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[Planner] LLM 规划异常：{ex.Message}，回退规则计划。");
        }

        return fallback;
    }

    /// <summary>返回 (计划, 思考过程)。思考过程来自推理模型的 reasoning_content，只用于展示。</summary>
    private async Task<(AgentPlan? Plan, string? Reasoning)> TryLlmPlanAsync(
        string query, CancellationToken ct, Action<string>? onReasoningDelta = null)
    {
        const string sampleJson = """
{
  "goal": "一句话重述用户目标",
  "steps": [
    { "id": "s1", "type": "skill", "name": "recall", "reason": "为什么用这个能力" }
  ],
  "stop_condition": "何时停止"
}
""";

        var system = "你是\"回忆助手\"的任务规划器。根据用户的问题，从可用能力中挑选 1.."
            + _maxSteps + " 个步骤组成执行计划。\n（内部思考/reasoning 请用中文，用户会直接看到你的判断依据。）\n"
            + PromptTime.Line() + "\n\n可用能力：\n"
            + Catalog.ToPromptList()
            + "\n\n规则：\n"
            + "1. 只能使用上面列出的能力名，不能发明新能力。\n"
            + "2. 步骤按执行顺序排列；能力不足时克制，别堆步骤。\n"
            + "3. 闲聊类问题返回单个 finish 步骤。\n"
            + "4. 需要\"多步查证\"（先按关键词找、再读原文、或统计后核对）时优先选 tools：\n"
            + "   它会让执行模型自己挑具体工具（search_messages/read_messages/get_session_stats 等）并多轮迭代；\n"
            + "   只是\"回忆某段内容/某人说过什么\"则用 recall。\n"
            + "4b. 问到\"你自己\"的问题（你是谁、你有哪些能力/工具/任务、你现在在哪个页面、自检一下）也选 tools：\n"
            + "   它里面有环境查询工具（get_environment_context / list_available_tools / list_missions / self_check）。\n"
            + "4c. 只有真的需要用户拍板时才把问题交出去（多个候选选一个、不可逆动作之前、用户明确要你确认）：\n"
            + "   那种情况选 tools，它会调 request_user_confirmation。\n"
            + "   **不要**因为「怕做错」就对只读查询或你已经能确定的事反复征求同意——该查就查、该答就答。\n"
            + "4d. 用户要求**给某人发消息 / 回复 / 打开某个微信会话**（写操作）时，一律选 tools：\n"
            + "   由执行模型自己调 wechat_send_message / wechat_open_chat 并填写参数（收件人、正文），\n"
            + "   它会先用会话目录核对收件人，执行前一定会弹确认。\n"
            + "   不要因为要发消息就选别的能力——发消息没有「固定句型」这一说，交给模型自己理解才对。\n"
            + "5. 只输出 JSON，不要任何解释或多余文字。格式：\n"
            + sampleJson;

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = system },
            new() { Role = "user", Content = $"用户问题：{query}\n\n请给出计划 JSON。" },
        };
        // 有思考回调就流式：用户能先看到"模型在权衡用哪个能力"
        var result = onReasoningDelta is not null && _chat is IStreamingChatClient streaming
            ? await streaming.ChatStreamAsync(messages, null, null, onReasoningDelta, ct)
            : await _chat!.ChatAsync(messages, tools: null, ct);
        if (string.IsNullOrWhiteSpace(result.Content)) return (null, result.ReasoningContent);

        return (PlanJsonParser.Parse(result.Content!), result.ReasoningContent);
    }
}
