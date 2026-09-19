namespace MemoryAssistant.Core.Agent.Planner;

/// <summary>
/// 规则规划器（确定性回退，零 LLM）：按 intent/关键词给出最安全的初始计划。
/// 计划步骤全部指向 SkillCatalog 中存在的 Skill，保证可通过 PlanValidator。
/// 只做"提示级"归类；真正的目标理解由 LLM Planner（可选）完成。
/// </summary>
public static class RulePlanner
{
    private static readonly string[][] StatsWords =
    [
        ["多久", "几次", "多少次", "排行", "最多", "经常", "频繁", "频率", "统计", "多少条", "量", "几次"],
    ];

    private static readonly string[][] StatsTimeWords =
    [
        ["时间", "几点", "时段", "深夜", "半夜", "凌晨", "晚上", "白天", "周末", "工作日"],
    ];

    // 仅"强时间定位"类词才触发 timeline；"去年/上周/上个月"等常出现在内容回忆问题里，
    // 若放进来会把"聊过什么"误判成纯时间线，故不放。
    private static readonly string[][] TimelineWords =
    [
        ["什么时候", "哪一天", "哪天聊", "几号", "当天", "时间线", "按天", "按日期", "时间分布"],
    ];

    private static readonly string[][] CommitmentWords =
    [
        ["承诺", "答应", "还没做", "没做", "待办", "说好要", "欠", "答应过", "flag"],
    ];

    private static readonly string[][] TopicWords =
    [
        ["话题", "主题", "在聊什么", "聊什么", "热点", "总结一下", "都在聊", "主题聚类", "都在讨论", "讨论什么"],
    ];

    private static readonly string[][] ProfileWords =
    [
        ["画像", "这个人", "那个人的", "他是什么样", "她的情况", "会话画像", "人物"],
    ];

    private static readonly string[][] ChitchatWords =
    [
        ["你好", "您好", "在吗", "谢谢", "再见", "你是谁", "你能做什么", "哈哈哈", "哈哈", "hi", "hello"],
    ];

    /// <summary>
    /// 情绪/寒暄类短句（"今天好累""晚安"）：真人会先接住情绪，而不是去翻记录。
    /// 只在整句很短时才当闲聊，避免"我最近聊得开心的那件事"被误判。
    /// </summary>
    private static readonly string[][] SocialWords =
    [
        ["晚安", "早安", "早上好", "中午好", "晚上好", "在干嘛", "干嘛呢", "无聊", "好累", "累了", "心累",
         "郁闷", "难受", "难过", "太烦", "烦死", "辛苦你了", "谢谢你", "没事了", "随便聊聊"],
    ];

    private static readonly string[][] WechatWords =
    [
        ["微信窗口", "当前聊天", "屏幕上", "看看微信", "微信在聊", "正在聊什么", "谁在发消息"],
    ];

    private static readonly string[][] ToolWords =
    [
        ["调用工具", "用工具", "工具调用", "tool:"],
    ];

    /// <summary>
    /// 关于"我自己"的问题（第三阶段 §16）：这些都是环境感知的问题，
    /// 必须交给 tools Skill 去调 get_environment_context / list_available_* / list_missions / self_check。
    /// </summary>
    private static readonly string[][] SelfAwarenessWords =
    [
        [
            "你是谁", "你是什么", "你的身份", "自我认知",
            "你有哪些能力", "你有什么能力", "你能做什么", "你会做什么", "你的能力",
            "你有哪些工具", "你有什么工具", "你有哪些技能", "你有哪些工作流", "你的工作流",
            "你有哪些任务", "你有什么任务", "你的任务",
            "自我检查", "自检", "检查一下你", "你还正常吗", "你现在在哪", "你现在在哪个页面",
        ],
    ];

    /// <summary>"和某人聊过/说过/提过"这类以人为锚点的回忆动词。</summary>
    private static readonly string[] PersonRecallVerbs =
        ["聊过", "聊了", "说过", "提过", "沟通", "联系过", "认识"];

    /// <summary>动作词：出现这些词说明用户想"查东西"，不能当闲聊（"我最近聊得好累"不是寒暄）。</summary>
    private static readonly string[][] ContentishWords =
    [
        ["查", "找", "看", "聊", "说", "统计", "总结", "帮", "搜", "记录", "提醒", "追踪", "有哪些"],
    ];

    /// <summary>依据 hint.intent + 关键词规则给出计划。</summary>
    public static AgentPlan RulePlan(string query, PlannerHint? hint)
    {
        var text = (query ?? "").Trim();
        var goal = string.IsNullOrWhiteSpace(text) ? "未识别的查询" : text;

        // 0. 显式写操作指令（打开X的聊天 / 给X发消息：Y / 回复：Y）优先：
        //    写操作有副作用，交给 action Skill（内部强制人工确认），且不参与只读意图路由。
        // 0.5 省略收件人的发送追问（"再发送一句X"）：这里**只能看出"像要发送"**——
        //    收件人要靠会话上文补，而这段上下文只在 action Skill 手里（LastPerson），
        //    所以同样交给它：它认得出就真的发，认不出也会明确说"没说是发给谁"，不会被当成只读提问。
        if (MemoryAssistant.Core.Agent.Action.ActionIntentParser.TryParse(text) is not null
            || MemoryAssistant.Core.Agent.Action.ActionIntentParser.IsBareSendFollowUp(text))
            return Build(goal, [("action", "执行微信写操作（需人工确认）")]);

        // 1. 显式"看屏幕/当前微信窗口"指令优先于意图 hint：
        //    IntentRouter 无 wechat 意图（且无 LLM 时会兜底成 recall），此处用高辨识度关键词纠正。
        if (Match(text, WechatWords)) return Build(goal, [("wechat", "读取微信窗口当前聊天（只读）")]);

        // 1.1 显式"调用工具"指令（V3.5b）：交给 tools Skill 用 LLM 自主选工具（含热注册的外部工具）
        if (Match(text, ToolWords)) return Build(goal, [("tools", "调用已注册工具完成任务")]);

        // 1.1b 关于"我自己"的问题（第三阶段 §16）：必须走 tools Skill 去调环境查询工具，
        //      否则会被当成闲聊，回一段"我是一个 AI 助手"的空话——而用户问的是这个软件里的我。
        if (Match(text, SelfAwarenessWords))
            return Build(goal, [("tools", "调用环境/能力查询工具回答关于我自己的问题")]);

        // 1.2 任务编排指令（V4.0）：把一句话需求固化成可长期执行的任务（草案交用户确认后创建）
        //     例外：任务执行链路会把"任务目标"整句喂回给 Agent（见 ConversationMissionExecutor）。
        //     任务目标里天然带"每天/定期"这类周期词，原样判断会把"执行任务"又当成"建任务"，
        //     于是回一句任务草案就算跑完了（实测 0 条证据 / 89ms / 一句正事没干）。
        //     带执行标记的文本直接跳过这一支，让它走下面的能力路由去真的干活。
        if (!IsMissionExecution(text) && MemoryAssistant.Core.Missions.MissionIntentParser.TryParse(text) is not null)
            return Build(goal, [("mission", "把需求编排成可执行任务（草案交用户确认）")]);

        // 1.3 寒暄/情绪类短句（"今天好累""晚安"）：真人先接住情绪，而不是去翻记录。
        //     只在整句短、且不含"查/找/看/聊/帮"这类动作词时才当闲聊，避免误判内容类提问。
        if (text.Length <= 16 && Match(text, SocialWords) && !Match(text, ContentishWords))
            return Build(goal, [], finish: true);

        // 2. 点名了具体的人 + "聊过/说过"→ 以人为锚点做回忆检索。
        //    这类问题被"最近聊/都在聊"之类泛化词抢走的话，答案里一个人名都不会出现。
        if (hint?.Entity is { Length: > 0 } person && IsPersonRecall(text))
            return Build(goal, [("recall", $"围绕「{person}」回忆相关聊天")]);

        // 2.1 Intent Hint（来自低成本的 IntentRouter），映射到最相关 Skill
        if (hint?.HasValue == true)
        {
            var intent = hint.Intent?.Trim();
            if (IsIntent(intent, "stats")) return Build(goal, [("stats", "先做统计，看量级")], statsOrTime: Match(text, StatsTimeWords));
            if (IsIntent(intent, "commitment")) return Build(goal, [("commitment", "查我答应过/还没做的事")]);
            if (IsIntent(intent, "chitchat")) return Build(goal, [], finish: true);
            if (IsIntent(intent, "recall"))
            {
                // recall 是"兜底意图"：hint 只是低成本提示，文本里出现更强的能力信号时以文本为准。
                // （否则"我最近和谁聊得最多"会被 recall 吃掉，统计能力永远用不上。）
                if (Match(text, StatsWords))
                    return Build(goal, [("stats", "先做统计，看量级")], statsOrTime: Match(text, StatsTimeWords));
                if (Match(text, CommitmentWords)) return Build(goal, [("commitment", "查我答应过/还没做的事")]);
                if (Match(text, TopicWords)) return Build(goal, [("topic", "分析高频主题")]);
                if (Match(text, ProfileWords)) return Build(goal, [("profile", "查看会话画像")]);
                if (Match(text, TimelineWords)) return Build(goal, [("timeline", "按时间范围定位")]);
                return Build(goal, [("recall", "语义召回定位会话与原文")]);
            }
            if (IsIntent(intent, "timeline")) return Build(goal, [("timeline", "按时间范围定位")]);
            if (IsIntent(intent, "topic")) return Build(goal, [("topic", "分析高频主题")]);
            if (IsIntent(intent, "profile")) return Build(goal, [("profile", "查看会话画像")]);
            if (IsIntent(intent, "wechat")) return Build(goal, [("wechat", "读取微信窗口当前聊天（只读）")]);
        }

        // 3. 关键词兜底
        if (Match(text, ChitchatWords)) return Build(goal, [], finish: true);
        if (Match(text, CommitmentWords)) return Build(goal, [("commitment", "查我答应过/还没做的事")]);
        if (Match(text, TopicWords)) return Build(goal, [("topic", "分析高频主题")]);
        if (Match(text, ProfileWords)) return Build(goal, [("profile", "查看会话画像")]);
        if (Match(text, StatsWords))
            return Build(goal, [("stats", "先做统计，看量级")], statsOrTime: Match(text, StatsTimeWords));
        if (Match(text, TimelineWords)) return Build(goal, [("timeline", "按时间范围定位")]);

        // 4. 默认：语义回忆
        return Build(goal, [("recall", "默认：语义召回定位会话与原文")]);
    }

    private static bool IsIntent(string? intent, string name)
        => intent is not null && intent.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 任务执行标记：任务执行器把"任务目标"喂给 Agent 时写在开头。
    /// 规则规划器见到它就不再把它当"建任务"指令（两者共用这一个常量，避免两边写两套字符串）。
    /// </summary>
    public const string MissionExecutionMarker = "【例行任务执行】";

    private static bool IsMissionExecution(string text)
        => text.StartsWith(MissionExecutionMarker, StringComparison.Ordinal);

    /// <summary>是不是"围绕某个人回忆聊天"的问题（排除统计/承诺/画像/时间线等有专门能力的情况）。</summary>
    private static bool IsPersonRecall(string text)
        => PersonRecallVerbs.Any(w => text.Contains(w, StringComparison.Ordinal))
           && !Match(text, StatsWords)
           && !Match(text, CommitmentWords)
           && !Match(text, ProfileWords)
           && !Match(text, TimelineWords);

    private static bool Match(string text, string[][] wordGroups)
        => wordGroups.Any(group => group.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)));

    private static AgentPlan Build(string goal, (string Skill, string Reason)[] skills, bool statsOrTime = false, bool finish = false)
    {
        var steps = new List<PlanStep>();
        int n = 0;
        if (!finish)
        {
            foreach (var (skill, reason) in skills)
                steps.Add(new PlanStep { Id = $"s{++n}", Kind = PlanStepKind.Skill, Name = skill, Reason = reason });
            // stats 又带时间语义 → 追加时间线验证（plan2 Scenario B 的雏形）
            if (statsOrTime && skills.Length > 0 && skills[^1].Skill == "stats")
                steps.Add(new PlanStep { Id = $"s{++n}", Kind = PlanStepKind.Skill, Name = "timeline", Reason = "验证时间分布" });
        }
        else
        {
            steps.Add(new PlanStep { Id = "s1", Kind = PlanStepKind.Finish, Name = "answer", Reason = "闲聊：不查聊天记录，直接回答" });
        }

        return new AgentPlan
        {
            Goal = goal,
            Steps = steps,
            FromRules = true,
            StopCondition = finish ? "直接回答即可" : "证据足够即可回答",
        };
    }
}
