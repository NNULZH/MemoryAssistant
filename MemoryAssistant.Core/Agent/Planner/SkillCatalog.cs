namespace MemoryAssistant.Core.Agent.Planner;

/// <summary>一个可用 Skill 的声明（能力描述供 Planner/LLM 选择）。</summary>
public sealed record SkillInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "Memory"; // Memory | Search | Analysis | Context

    /// <summary>
    /// 对 LLM 规划器隐藏：这个能力不进"可用能力清单"，模型选不到它。
    ///
    /// 为什么需要这个开关（实测教训）：规划是**交给 AI 的**（LLM 规划器），但有些能力的**实现是纯规则**的
    /// （典型：`action` 用正则解析用户原话）。于是出现最难受的一种错配——
    /// AI 完全听懂了用户的话、正确地把任务归给"发消息"，可它选中的那个能力却拿正则再理解一遍，
    /// 句式一超纲就静默空转（还容易被作答器讲成"没等到回执"）。
    /// 写操作这类事应该由 AI 全程主导（选工具 + 填参数），规则实现只当"没有模型时的兜底"，
    /// 因此在清单里藏起来，但**能力本身仍然注册**——不然"我有哪些能力"这类回答会漏掉它。
    /// </summary>
    public bool LlmHidden { get; init; }
}

/// <summary>
/// 可用 Skill 目录（plan2 §5 列出的能力；P13 给每个 Skill 提供实现，
/// P12 阶段 Planner 只负责"选择"，不执行）。
/// </summary>
public sealed class SkillCatalog
{
    private readonly Dictionary<string, SkillInfo> _skills = new(StringComparer.Ordinal);

    public SkillCatalog() { }

    /// <summary>默认目录：与 plan2 §5 Skill 对齐（顺序即 UI/提示呈现顺序）。</summary>
    public static SkillCatalog Default() => new SkillCatalog()
        .Register("recall", "语义召回聊天内容并取证（找某段聊天/某人说过什么，默认首选）", "Memory")
        .Register("stats", "会话统计：发言排行/活跃时间/消息量", "Analysis")
        .Register("timeline", "按日期/时间范围查询与时间线", "Search")
        .Register("commitment", "查找承诺/待办（我答应过什么/还没做什么）", "Memory")
        .Register("topic", "话题分析：一段时间的高频主题与聚类", "Analysis")
        .Register("profile", "人物/会话画像：消息数、活跃时段、高频话题", "Analysis")
        .Register("wechat", "读取微信窗口当前可见聊天（只读，实时上下文）", "Context")
        // 写操作（打开会话/发送消息）：**对 LLM 规划器隐藏**（llmHidden: true）。
        // 它不是"AI 的一双手"，而是"没有模型时的兜底"——实现是正则解析用户原话，
        // 只要句式超出那几条正则就静默空转。让 AI 选它 = 让 AI 把活交给一个听不懂人话的东西。
        // AI 主导的写法是选 tools，由模型自己调 wechat_send_message / wechat_open_chat（参数它自己填，
        // 内部有确认弹窗 + 白名单 + 会话目录核对三道闸）。
        .Register("action", "执行微信写操作：打开会话/发送消息（需人工确认）", "Action", llmHidden: true)
        // 让模型知道 tools 是"自主选工具"的入口：需要按关键词检索/读原文/统计等具体动作时，
        // 选它比选固定能力更灵活（它能自己决定调 search_messages 还是 read_messages 并多轮迭代）。
        // 第三阶段起它同时承担"关于我自己的问题"：环境/能力/任务清单/自检都通过它去调对应工具。
        .Register("tools", "自主选工具完成任务：检索/统计/读原文、**发微信消息/打开会话**（写操作，会弹确认）、"
                         + "外部工具（多步查证首选）；"
                         + "也用于回答关于我自己的问题（你是谁、有哪些能力/工具/任务、系统自检）；"
                         + "真正需要用户拍板时用它调 request_user_confirmation（多候选二选一、不可逆动作之前）", "Context")
        .Register("mission", "把一句话需求编排成可执行任务（追踪会话/定时汇总），确认后创建", "Action")
        .Register("chitchat", "闲聊寒暄：不查聊天记录，直接回答", "Context");

    public SkillCatalog Register(string name, string description, string category = "Memory", bool llmHidden = false)
    {
        _skills[name] = new SkillInfo { Name = name, Description = description, Category = category, LlmHidden = llmHidden };
        return this;
    }

    public bool Contains(string name) => _skills.ContainsKey(name);

    public SkillInfo? TryGet(string name) => _skills.GetValueOrDefault(name);

    public IReadOnlyCollection<SkillInfo> All => _skills.Values;

    /// <summary>
    /// 紧凑能力清单文本（喂给 LLM 规划提示；描述截断控 token）。
    /// 对模型隐藏的能力（<see cref="SkillInfo.LlmHidden"/>）不出现在这里——它们不是给 AI 用的，
    /// 而是"没有模型时的兜底"（见 SkillInfo.LlmHidden 的说明）。
    /// </summary>
    public string ToPromptList()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in _skills.Values)
        {
            if (s.LlmHidden) continue;
            var desc = s.Description.Length <= 60 ? s.Description : s.Description[..60] + "...";
            sb.AppendLine($"- {s.Name}: {desc}");
        }
        return sb.ToString().TrimEnd();
    }
}
