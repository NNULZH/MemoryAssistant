using MemoryAssistant.Core.Agent.Query;

namespace MemoryAssistant.Tests;

/// <summary>P18 目标抽取单测：实体/时间/意图/证据要求/约束（零 LLM，纯规则）。</summary>
public sealed class QueryUnderstandingTests
{
    private static readonly RuleQueryAnalyzer Analyzer = new();

    [Fact]
    public void RecallQuery_ExtractsEntityTimeAndEvidenceRequirement()
    {
        var m = Analyzer.Analyze("我和小王去年聊工作的记录里，有没有提到秋招？");

        Assert.Equal("recall", m.IntentHint);
        Assert.Contains("小王", m.Entities);
        Assert.Equal(TimeKind.LastYear, m.TimeKind);
        Assert.Equal("去年", m.TimePhrase);
        Assert.True(m.NeedsOriginalEvidence);
        Assert.Contains("仅查聊天记录", m.Constraints);
        Assert.Equal("我和小王去年聊工作的记录里，有没有提到秋招？", m.Goal);
    }

    [Fact]
    public void StatsQuery_DetectsRecentAndStatsIntent()
    {
        var m = Analyzer.Analyze("我最近是不是经常半夜和人聊天？");
        Assert.Equal("stats", m.IntentHint);
        Assert.Equal(TimeKind.Recent, m.TimeKind);
        Assert.Equal("最近", m.TimePhrase);
    }

    [Fact]
    public void SpecificDate_IsDetected()
    {
        var m = Analyzer.Analyze("2026年3月2日我和谁聊过天？");
        Assert.Equal(TimeKind.SpecificDate, m.TimeKind);
        Assert.Contains("2026年3月2日", m.TimePhrase);
    }

    [Fact]
    public void CommitmentAndTopicAndProfile_MapToIntents()
    {
        Assert.Equal("commitment", Analyzer.Analyze("我答应过别人什么还没做？").IntentHint);
        Assert.Equal("topic", Analyzer.Analyze("最近大家都在聊什么话题？").IntentHint);
        Assert.Equal("profile", Analyzer.Analyze("同学群是个什么样的群？").IntentHint);
    }

    [Fact]
    public void Chitchat_MapsToChitchat()
    {
        Assert.Equal("chitchat", Analyzer.Analyze("你好，谢谢！").IntentHint);
    }

    [Fact]
    public void QuotedKeyword_IsCollected()
    {
        var m = Analyzer.Analyze("有没有人提过“秋招”这件事？");
        Assert.Contains("秋招", m.Keywords);
    }

    [Fact]
    public void EmptyQuery_DoesNotThrow()
    {
        var m = Analyzer.Analyze("  ");
        Assert.Equal("（空查询）", m.Goal);
        Assert.False(m.NeedsOriginalEvidence);
    }

    [Fact]
    public void ToHint_CarriesIntentEntityTime()
    {
        var m = Analyzer.Analyze("我和小明上周聊过工作吗？");
        var h = m.ToHint();
        Assert.Equal("recall", h.Intent);
        Assert.Equal("小明", h.Entity);
        Assert.Equal("上周", h.TimeHint);
    }
}
