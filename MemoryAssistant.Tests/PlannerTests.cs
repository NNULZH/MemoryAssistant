using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Configuration;

namespace MemoryAssistant.Tests;

/// <summary>
/// P12 Planner 单测：JSON 解析 / 校验 / 规则规划 / LLM→校验→规则回退（零真实 LLM）。
/// </summary>
public sealed class PlannerTests
{
    private static AgentOptions Options() => new() { MaxPlanSteps = 8 };
    private static SkillCatalog Catalog() => SkillCatalog.Default();
    private static PlanValidator Validator() => new(Catalog(), Options());

    private static AgentPlan ValidPlan() => new()
    {
        Goal = "回忆和同学的聊天",
        Steps =
        [
            new PlanStep { Id = "s1", Kind = PlanStepKind.Skill, Name = "recall", Reason = "语义召回" },
        ],
    };

    // ---------- PlanJsonParser ----------

    [Fact]
    public void Parser_PlainJson_Ok()
    {
        var plan = PlanJsonParser.Parse("""{"goal":"回忆","steps":[{"id":"s1","type":"skill","name":"recall","reason":"先找"}]}""");
        Assert.NotNull(plan);
        Assert.Equal("回忆", plan!.Goal);
        Assert.Single(plan.Steps);
        Assert.Equal("recall", plan.Steps[0].Name);
        Assert.Equal("skill", plan.Steps[0].Kind);
    }

    [Fact]
    public void Parser_FencedJson_Ok()
    {
        var text = """
        好的，计划如下：
        ```json
        {"goal":"统计深夜聊天","steps":[{"id":"s1","type":"skill","name":"stats"},{"id":"s2","type":"skill","name":"timeline"}]}
        ```
        """;
        var plan = PlanJsonParser.Parse(text);
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Steps.Count);
        Assert.Equal("stats", plan.Steps[0].Name);
    }

    [Fact]
    public void Parser_BareJsonWithProse_Ok()
    {
        var text = "计划：\n{\"goal\":\"g\",\"steps\":[]}\n结束";
        var plan = PlanJsonParser.Parse(text);
        Assert.NotNull(plan);
        Assert.Equal("g", plan!.Goal);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Parser_Malformed_ReturnsNull()
    {
        Assert.Null(PlanJsonParser.Parse("不是 JSON"));
        Assert.Null(PlanJsonParser.Parse(""));
        Assert.Null(PlanJsonParser.Parse("{\"steps\": [}"));
    }

    // ---------- PlanValidator ----------

    [Fact]
    public void Validator_ValidPlan_Passes()
    {
        var v = Validator().Validate(ValidPlan());
        Assert.True(v.Ok);
        Assert.Empty(v.Errors);
    }

    [Fact]
    public void Validator_DuplicateId_RejectedAndRepaired()
    {
        var bad = ValidPlan() with
        {
            Steps =
            [
                new PlanStep { Id = "s1", Kind = PlanStepKind.Skill, Name = "recall" },
                new PlanStep { Id = "s1", Kind = PlanStepKind.Skill, Name = "stats" },
            ],
        };
        var v = Validator().Validate(bad);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("重复"));
        Assert.Single(v.Repaired!.Steps);
        Assert.Equal("recall", v.Repaired.Steps[0].Name);
    }

    [Fact]
    public void Validator_UnknownSkill_RejectedAndDropped()
    {
        var bad = ValidPlan() with
        {
            Steps =
            [
                new PlanStep { Id = "s1", Kind = PlanStepKind.Skill, Name = "recall" },
                new PlanStep { Id = "s2", Kind = PlanStepKind.Skill, Name = "hack_memory" },
            ],
        };
        var v = Validator().Validate(bad);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("hack_memory"));
        Assert.Single(v.Repaired!.Steps);
    }

    [Fact]
    public void Validator_UnknownKind_Rejected()
    {
        var bad = ValidPlan() with
        {
            Steps = [new PlanStep { Id = "s1", Kind = "tool", Name = "delete_all" }],
        };
        var v = Validator().Validate(bad);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("tool"));
    }

    [Fact]
    public void Validator_EmptySteps_Rejected()
    {
        var v = Validator().Validate(new AgentPlan { Goal = "g" });
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("没有步骤"));
    }

    [Fact]
    public void Validator_TooManySteps_Rejected()
    {
        var opts = new AgentOptions { MaxPlanSteps = 3 };
        var validator = new PlanValidator(Catalog(), opts);
        var steps = Enumerable.Range(1, 4)
            .Select(i => new PlanStep { Id = $"s{i}", Kind = PlanStepKind.Skill, Name = "recall" })
            .ToList();
        var v = validator.Validate(new AgentPlan { Goal = "g", Steps = steps });
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("超过上限"));
    }

    // ---------- RulePlanner（零 LLM 确定性回退） ----------

    [Fact]
    public void RulePlanner_DefaultQuery_PlansRecall()
    {
        var plan = RulePlanner.RulePlan("我上次和小明聊了什么？", hint: null);
        Assert.Contains(plan.Steps, s => s.Kind == PlanStepKind.Skill && s.Name == "recall");
    }

    [Fact]
    public void RulePlanner_StatsWithTimeWord_AddsTimeline()
    {
        var plan = RulePlanner.RulePlan("我最近是不是经常半夜和人聊天？", hint: null);
        var names = plan.Steps.Where(s => s.Kind == PlanStepKind.Skill).Select(s => s.Name).ToList();
        Assert.Contains("stats", names);
        Assert.Contains("timeline", names);
    }

    [Fact]
    public void RulePlanner_PersonQuestion_UsesRecall_EvenWithTopicHint()
    {
        // "最近聊过什么"曾被"最近聊"这类泛化词带去话题分析，答案里连人名都不出现
        var plan = RulePlanner.RulePlan("我和张晓明最近聊过什么？", new PlannerHint("topic", "张晓明", "最近"));
        Assert.Equal("recall", plan.Steps[0].Name);
    }

    [Fact]
    public void RulePlanner_PersonStatsQuestion_KeepsStats()
    {
        var plan = RulePlanner.RulePlan("我和张晓明一共聊了多少次？", new PlannerHint("stats", "张晓明", null));
        Assert.Equal("stats", plan.Steps[0].Name);
    }

    [Fact]
    public void RulePlanner_Chitchat_PlansFinish()
    {
        var plan = RulePlanner.RulePlan("你好，谢谢", hint: null);
        Assert.Single(plan.Steps);
        Assert.Equal(PlanStepKind.Finish, plan.Steps[0].Kind);
    }

    [Fact]
    public void RulePlanner_Commitment_PlansCommitment()
    {
        var plan = RulePlanner.RulePlan("我还有哪些事没做？", hint: null);
        Assert.Contains(plan.Steps, s => s.Kind == PlanStepKind.Skill && s.Name == "commitment");
    }

    [Fact]
    public void RulePlanner_HintOverridesKeyword()
    {
        // 无关键词但有 intent=commitment hint，也应规划 commitment。
        var plan = RulePlanner.RulePlan("帮我看一下", new PlannerHint("commitment", null, null));
        Assert.Contains(plan.Steps, s => s.Kind == PlanStepKind.Skill && s.Name == "commitment");
    }

    [Fact]
    public void RulePlan_AlwaysPassesValidator()
    {
        var validator = Validator();
        string[] queries = ["我和谁聊过秋招", "最近常和谁聊天", "我答应过什么", "上周聊了什么", "你好", "总结一下最近话题"];
        foreach (var q in queries)
        {
            var plan = RulePlanner.RulePlan(q, hint: null);
            Assert.True(validator.Validate(plan).Ok, $"规则计划应始终有效：{q}");
        }
    }

    // ---------- Planner（LLM → 校验 → 规则回退） ----------

    [Fact]
    public async Task Planner_NoChatClient_UsesRules()
    {
        var planner = new Planner(Catalog(), Options(), chat: null);
        var plan = await planner.PlanAsync("我最近和谁聊过工作？");
        Assert.True(plan.FromRules);
        Assert.False(plan.FromLlm);
    }

    [Fact]
    public async Task Planner_LlmValidFencedPlan_Accepted()
    {
        var script = new ChatResult
        {
            Content = "```json\n{\"goal\":\"统计\",\"steps\":[{\"id\":\"s1\",\"type\":\"skill\",\"name\":\"stats\",\"reason\":\"先统计\"}]}\n```",
        };
        var chat = new ScriptedChatClient([script]);
        var planner = new Planner(Catalog(), Options(), chat);
        var plan = await planner.PlanAsync("统计一下聊天");
        Assert.True(plan.FromLlm);
        Assert.Contains(plan.Steps, s => s.Name == "stats");
    }

    [Fact]
    public async Task Planner_LlmInvalidSkill_FallsBackToRules()
    {
        var script = new ChatResult
        {
            Content = "{\"goal\":\"g\",\"steps\":[{\"id\":\"s1\",\"type\":\"skill\",\"name\":\"not_a_skill\"}]}",
        };
        var chat = new ScriptedChatClient([script]);
        var planner = new Planner(Catalog(), Options(), chat);
        var plan = await planner.PlanAsync("随机问题");
        Assert.False(plan.FromLlm);
        Assert.True(plan.FromRules);
    }

    [Fact]
    public async Task Planner_LlmUselessOutput_FallsBackToRules()
    {
        var chat = new ScriptedChatClient([]); // 脚本空 → 返回无意义文本，走规则回退
        var planner = new Planner(Catalog(), Options(), chat);
        var plan = await planner.PlanAsync("abc");
        Assert.True(plan.FromRules);
    }

    [Fact]
    public async Task Planner_ExposesCatalog()
    {
        var planner = new Planner(Catalog(), Options());
        Assert.True(planner.Catalog.Contains("recall"));
        Assert.True(planner.Catalog.Contains("chitchat"));
        Assert.True(planner.Catalog.Contains("wechat"));
        Assert.True(planner.Catalog.Contains("action"));   // V3.4 写操作
        Assert.True(planner.Catalog.Contains("tools"));    // V3.5 通用工具调用
        Assert.True(planner.Catalog.Contains("mission"));  // V4.0 对话式任务编排
        Assert.Equal(11, planner.Catalog.All.Count);
    }

    // ---------- 写操作：AI 主导，不给模型"规则技能"这个选项 ----------

    [Fact]
    public void Catalog_LlmPromptList_HidesWriteRuleSkill()
    {
        var catalog = Catalog();
        var prompt = catalog.ToPromptList();

        // 模型看不到 action：它的实现是正则解析，句式一超纲就静默空转，
        // 让"已经听懂人话的 AI"把活交给它，是实测踩过的最难受的错配。
        Assert.DoesNotContain("- action", prompt);
        Assert.True(catalog.Contains("action"));                       // 能力本身还在（"我会什么"要答得全）
        Assert.True(catalog.TryGet("action")!.LlmHidden);
        Assert.False(catalog.TryGet("tools")!.LlmHidden);              // 写操作走它
    }

    [Fact]
    public void Catalog_LlmPromptList_TellsModelThatToolsCanSendMessages()
    {
        // 藏掉 action 之后，模型必须能从 tools 的描述里知道"发消息是它干"
        var prompt = Catalog().ToPromptList();

        Assert.Contains("tools", prompt);
        Assert.Contains("发微信消息", prompt);
    }
}
