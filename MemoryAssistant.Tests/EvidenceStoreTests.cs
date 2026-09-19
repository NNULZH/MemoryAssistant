using MemoryAssistant.Core.Agent;

namespace MemoryAssistant.Tests;

/// <summary>
/// P16 EvidenceStore 单测：稳定编号/内容去重/生命周期迁移/事实-推测分离。
/// 全部纯内存，零 LLM / 零数据。
/// </summary>
public sealed class EvidenceStoreTests
{
    private static Evidence E(string content, string source = "微信原文", string session = "s1")
        => new() { SessionId = session, SessionDisplayName = session, Content = content, Source = source };

    [Fact]
    public void Observe_AssignsSequentialIndexes()
    {
        var store = new EvidenceStore();
        store.Observe(E("a"));
        store.Observe(E("b"));
        store.Observe(E("c"));

        Assert.Equal(3, store.Count);
        Assert.Equal([1, 2, 3], store.Items.Select(x => x.Index).ToArray());
    }

    [Fact]
    public void Observe_DeduplicatesByContent()
    {
        var store = new EvidenceStore();
        store.Observe(E("same"));
        store.Observe(E("same"));
        store.Observe(E("same"), originTool: "recall");

        Assert.Single(store.Items);
        Assert.Equal(1, store.Items[0].Index);
    }

    [Fact]
    public void Lifecycle_ObservedToVerifiedToCited()
    {
        var store = new EvidenceStore();
        store.Observe(E("x"));
        Assert.Equal(EvidenceStage.Observed, store.Items[0].Stage);
        Assert.False(store.Items[0].Verified);

        Assert.True(store.Verify(1));
        Assert.Equal(EvidenceStage.Verified, store.Items[0].Stage);
        Assert.True(store.Items[0].Verified);

        store.MarkCited();
        Assert.Equal(EvidenceStage.Cited, store.Items[0].Stage);
        Assert.True(store.Items[0].Verified);
    }

    [Fact]
    public void VerifyAll_MarksRemainingAsVerified()
    {
        var store = new EvidenceStore();
        store.Observe(E("a"));
        store.Observe(E("b"));
        store.Verify(1);

        Assert.Equal(1, store.VerifyAll()); // 只新增核实 b
        Assert.All(store.Items, e => Assert.True(e.Verified));
    }

    [Fact]
    public void Kind_ClassifiedBySource()
    {
        Assert.Equal(EvidenceKind.Fact, EvidenceStore.ClassifyKind("微信原文"));
        Assert.Equal(EvidenceKind.Fact, EvidenceStore.ClassifyKind("rag"));
        Assert.Equal(EvidenceKind.Fact, EvidenceStore.ClassifyKind("承诺"));
        Assert.Equal(EvidenceKind.Inference, EvidenceStore.ClassifyKind("stats"));
        Assert.Equal(EvidenceKind.Inference, EvidenceStore.ClassifyKind("话题"));
    }

    [Fact]
    public void FactAndInference_FiltersSeparate()
    {
        var store = new EvidenceStore();
        store.Observe(E("原文消息", "微信原文"));   // Fact
        store.Observe(E("统计：会话 10 个", "stats")); // Inference

        Assert.Single(store.FactItems);
        Assert.Single(store.InferenceItems);
        Assert.Equal("原文消息", store.FactItems[0].Content);
    }

    [Fact]
    public void OriginTool_IsRecorded()
    {
        var store = new EvidenceStore();
        store.Observe(E("x"), originTool: "recall");
        Assert.Equal("recall", store.Items[0].OriginTool);
    }

    [Fact]
    public void AddCandidate_EntersAsCandidate_NotObserved()
    {
        var store = new EvidenceStore();
        store.AddCandidate(E("初步命中"));
        Assert.Equal(EvidenceStage.Candidate, store.Items[0].Stage);
        Assert.False(store.VerifiedItems.Any());
    }
}
