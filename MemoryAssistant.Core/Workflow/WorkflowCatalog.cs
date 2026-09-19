namespace MemoryAssistant.Core.Workflow;

/// <summary>工作流里的一个固定阶段（Agent 固定按这套阶段完成任务）。</summary>
public sealed record WorkflowStageInfo(
    int Index,
    string Name,
    string Component,
    string Description,
    bool Autonomous);

/// <summary>
/// 工作流目录（能力模型的一部分）。
///
/// 为什么要放在 Core：Agent 说"我有哪些工作流"和 GUI 画"固定阶段图"必须来自**同一份定义**
/// （规范 §41：Agent 看到的软件世界和用户看到的软件世界应该是同一个软件世界）。
/// 每一条都对应真实装配的组件，不是画着好看的示意图。
/// </summary>
public static class WorkflowCatalog
{
    public static IReadOnlyList<WorkflowStageInfo> Stages { get; } =
    [
        new(1, "用户输入", "对话页 · 输入框",
            "一句话提问，或把长期需求派给我。派活会先给出任务草案，你确认后才创建任务。",
            Autonomous: false),
        new(2, "理解意图", "IntentRouter + ConversationalAgent",
            "判断这是回忆聊天、统计、闲聊还是派活，决定要不要动工具、动哪个。",
            Autonomous: true),
        new(3, "规划步骤", "Planner（模型规划，失败自动回退规则规划）",
            "把问题拆成可执行的几步：先查哪个会话、再读哪段时间。",
            Autonomous: true),
        new(4, "调用工具", "ToolRegistry（真实注册表 + 参数校验）",
            "从工具清单里自主选工具、自己填参数。每次调用的参数与结果都会记录。",
            Autonomous: true),
        new(5, "检索记忆", "HybridRetriever（向量 + 全文）· BridgeMemoryBackend",
            "从个人聊天知识库召回相关片段，编号成证据，供后续引用与跳转。",
            Autonomous: false),
        new(6, "生成回答", "LlmAnswerComposer",
            "把查证过的证据讲成人话，并在句尾标 [N] 引用——点引用能直接跳到那段聊天。",
            Autonomous: false),
        new(7, "完成", "AgentResult（证据 / 轮次 / 耗时）",
            "汇总本轮产出；证据不足时会换策略重跑，而不是硬答。",
            Autonomous: false),
    ];

    /// <summary>给 Agent 看的文本版（list_available_workflows 工具用）。</summary>
    public static string ToPromptList()
    {
        var lines = Stages.Select(s =>
            $"{s.Index}. {s.Name}（{s.Component}）{(s.Autonomous ? " [Agent 自主决策]" : "")}：{s.Description}");
        return "对话式任务的实际执行流程（每次提问都会走一遍）：\n" + string.Join("\n", lines);
    }
}
