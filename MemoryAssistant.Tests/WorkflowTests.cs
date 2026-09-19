using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.RAG;
using MemoryAssistant.Core.Workflow;

namespace MemoryAssistant.Tests;

public class WorkflowTests
{
    // ---- Intent Router（规则兜底路径，不需要 LLM） ----

    [Theory]
    [InlineData("我还有哪些事情没做？", IntentKind.Commitment)]
    [InlineData("我答应过别人什么", IntentKind.Commitment)]
    [InlineData("我有什么待办", IntentKind.Commitment)]
    [InlineData("记得提醒我", IntentKind.Commitment)]
    [InlineData("我答应过谁什么事情？", IntentKind.Commitment)]
    [InlineData("帮我找之前提到社保的消息", IntentKind.Recall)]
    [InlineData("去年这个时候我在干嘛？", IntentKind.Recall)]
    [InlineData("这个群谁最活跃？", IntentKind.Stats)]
    [InlineData("最近一个月我主要和哪些人聊天？", IntentKind.Stats)]
    [InlineData("今天天气怎么样？", IntentKind.Chitchat)]
    [InlineData("你好", IntentKind.Chitchat)]
    public async Task RouteByRule_ClassifiesCorrectly(string query, IntentKind expected)
    {
        // LLM 失败时走规则兜底 —— 用抛异常的 client 模拟
        var failing = new ScriptedChatClient([]);
        // ScriptedChatClient 不抛异常，需要包装：直接调静态方法验证规则
        var intent = IntentRouterRuleOnly(query);
        Assert.Equal(expected, intent);
    }

    // 规则表是私有的，通过一个总是失败 LLM 的 router 触发兜底
    private static IntentKind IntentRouterRuleOnly(string query)
    {
        var chat = new ThrowingChatClient();
        var router = new IntentRouter(chat);
        var result = router.RouteAsync(query).GetAwaiter().GetResult();
        return result.Intent;
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public string ModelName => "throw";
        public Task<ChatResult> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSchema>? tools = null, CancellationToken ct = default)
            => throw new InvalidOperationException("LLM unavailable");
    }

    // ---- EvidenceMerger ----

    [Fact]
    public void FromChunks_AssignsSequentialIndexes()
    {
        var chunks = new[]
        {
            new RetrievedChunk { SessionId = "a", Date = "2026-09-01", Text = "x", SessionName = "A" },
            new RetrievedChunk { SessionId = "b", Date = "2026-09-02", Text = "y", SessionName = "B" },
        };
        var ev = EvidenceMerger.FromChunks(chunks, "rag");
        Assert.Equal(2, ev.Count);
        Assert.Equal(1, ev[0].Index);
        Assert.Equal(2, ev[1].Index);
        Assert.Equal("2026-09-01", DateTimeOffset.FromUnixTimeSeconds(ev[0].CreateTime).ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void FromChunks_DeduplicatesBySessionAndDate()
    {
        var chunks = new[]
        {
            new RetrievedChunk { SessionId = "a", Date = "2026-09-01", Text = "x", SessionName = "A" },
            new RetrievedChunk { SessionId = "a", Date = "2026-09-01", Text = "x2", SessionName = "A" },
            new RetrievedChunk { SessionId = "a", Date = "2026-09-02", Text = "y", SessionName = "A" },
        };
        var ev = EvidenceMerger.FromChunks(chunks, "rag");
        Assert.Equal(2, ev.Count);
    }

    [Fact]
    public void Merge_RemovesDuplicatesAndRenumbers()
    {
        var e1 = new[] { new Evidence { Index = 1, SessionId = "a", CreateTime = 100, Content = "x" } };
        var e2 = new[] { new Evidence { Index = 1, SessionId = "a", CreateTime = 100, Content = "x" } };
        var merged = EvidenceMerger.Merge(e1, e2);
        Assert.Single(merged);
        Assert.Equal(1, merged[0].Index);
    }

    // ---- Commitment（P7） ----

    [Fact]
    public void FromCommitmentCandidates_AssignsSequentialIndexes()
    {
        var candidates = new[]
        {
            new MemoryAssistant.Core.Features.Commitment.CommitmentCandidate("s1", "群A", "2026-08-11", 1756800000, "我", true, "到时候再看", "到时候再"),
            new MemoryAssistant.Core.Features.Commitment.CommitmentCandidate("s2", "B", "2026-08-16", 0, "xyc", false, "我明天看看", "我明天"),
        };
        var ev = EvidenceMerger.FromCommitmentCandidates(candidates);
        Assert.Equal(2, ev.Count);
        Assert.Equal(1, ev[0].Index);
        Assert.Equal(2, ev[1].Index);
        Assert.Equal("commitment", ev[0].Source);
        Assert.Equal("群A", ev[0].SessionDisplayName);
    }

    [Fact]
    public void FromCommitmentCandidates_DeduplicatesByIdentity()
    {
        var candidates = new[]
        {
            new MemoryAssistant.Core.Features.Commitment.CommitmentCandidate("s1", "A", "2026-08-11", 100, "我", true, "内容", "我来"),
            new MemoryAssistant.Core.Features.Commitment.CommitmentCandidate("s1", "A", "2026-08-11", 100, "我", true, "内容", "我来"),
        };
        var ev = EvidenceMerger.FromCommitmentCandidates(candidates);
        Assert.Single(ev);
    }

    // ---- 工具调用标记泄漏清洗 ----
    // 注：半角闭合标签一律用字符串拼接构造（"<" + "/tool_calls"），避免源码里出现裸闭合标签。

    private static readonly string CloseToolCalls = "<" + "/tool_calls>";
    private static readonly string CloseInvoke = "<" + "/invoke>";

    [Fact]
    public void StripDsmlMarkers_RemovesLeakedToolCallBlocks()
    {
        var raw = "我先看看候选。\n＜｜DSML｜｜tool_calls＞\n＜｜DSML｜｜invoke name=\"read_messages\"＞\n＜｜DSML｜｜parameter name=\"session_id\" string=\"true\"＞s1＜｜DSML｜｜parameter＞\n＜｜DSML｜｜/invoke＞\n＜｜DSML｜｜/tool_calls＞\n最终答案。";
        var cleaned = AgentLoop.StripDsmlMarkers(raw);
        Assert.DoesNotContain("DSML", cleaned);
        Assert.StartsWith("我先看看候选", cleaned);
        Assert.EndsWith("最终答案。", cleaned);
    }

    [Fact]
    public void StripDsmlMarkers_UnclosedTagDropped()
    {
        var cleaned = AgentLoop.StripDsmlMarkers("正文＜｜DSML｜｜invoke name=\"x\"＞");
        Assert.Equal("正文", cleaned);
    }

    [Fact]
    public void StripDsmlMarkers_NoMarker_Unchanged()
    {
        const string raw = "普通回答，没有标记。";
        Assert.Equal(raw, AgentLoop.StripDsmlMarkers(raw));
    }

    [Fact]
    public void StripDsmlMarkers_LiteralAngleBracket_Kept()
    {
        // 正文里普通的 "<" 不能被当成标记，否则整段回答会被误删
        const string raw = "3 < 5 是成立的。";
        Assert.Equal(raw, AgentLoop.StripDsmlMarkers(raw));
    }

    [Fact]
    public void StripDsmlMarkers_HalfWidthXmlBlock_RemovedKeepsSurroundingText()
    {
        // 实测形态：模型把"工具调用"写成了半角 XML 正文，而不是走协议 tool_calls
        var raw = "分析如下。\n<tool_calls>\n<invoke>\n"
            + "<parameter name=\"keyword\">镇江</parameter>\n" + CloseInvoke + "\n"
            + CloseToolCalls + "\n结论：聊过。";
        var cleaned = AgentLoop.StripDsmlMarkers(raw);

        Assert.DoesNotContain("tool_calls", cleaned);
        Assert.DoesNotContain("parameter", cleaned);
        Assert.Contains("分析如下。", cleaned);
        Assert.EndsWith("结论：聊过。", cleaned);
    }

    [Fact]
    public void StripDsmlMarkers_TruncatedHalfWidthBlock_DropsRest()
    {
        // 被 max_tokens 截断的半角块（结束标签缺失）→ 标记之后的内容全部丢弃，不留半截 XML
        var raw = "<tool_calls>\n<invoke>\n<parameter name=\"limit\">30</parameter>\n"
            + "<" + "/tool_" + "calls";
        var cleaned = AgentLoop.StripDsmlMarkers(raw);

        Assert.Equal("", cleaned);
    }
}
