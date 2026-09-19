using MemoryAssistant.Core.Missions;

namespace MemoryAssistant.Tests;

/// <summary>
/// V4.1 主题监听单测：模型扩词解析 + 优先队列（命中多的多扫）+ 画像（跨轮次攒分数）。
/// 全是纯函数，不碰 Bridge / 网络 / 时间。
/// </summary>
public sealed class TopicScanTests
{
    // ---------- 模型扩词 ----------

    /// <summary>模型输出带 ```json 围栏和前后说明时，也要能把相关词抠出来。</summary>
    [Fact]
    public void Keywords_ParesModelJson_WithFenceAndProse()
    {
        const string raw = """
            好的，我给出的相关词是：
            ```json
            {"keywords":["校招","内推","招聘","秋招","实习"]}
            ```
            这些词在聊天里比较常见。
            """;

        var list = TopicKeywords.Parse(raw, "就业信息");

        Assert.Equal(["校招", "内推", "招聘", "秋招", "实习"], list);
        Assert.DoesNotContain("就业信息", list);      // 模型给了词就用模型的，不硬塞主题
    }

    [Fact]
    public void Keywords_ReadsBareArray_AndDedupsAndCaps()
    {
        var list = TopicKeywords.Parse("""["校招","内推","校招","招","这是一个特别特别长的关键词不该要","秋招","实习","offer","补招"]""", "就业信息");

        Assert.Equal(6, list.Count);                  // 上限 6 个
        Assert.Equal(["校招", "内推", "秋招", "实习", "offer", "补招"], list);
    }

    /// <summary>模型抽风/没模型时退回主题本身——宁可用字面词继续扫，也不空转。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("我不知道怎么扩")]
    [InlineData("{\"keywords\":[]}")]
    public void Keywords_FallsBackToTopic_WhenUnparsable(string raw)
    {
        Assert.Equal(["就业信息"], TopicKeywords.Parse(raw, "就业信息"));
    }

    // ---------- 优先队列 ----------

    [Fact]
    public void Rank_GroupsBySession_AndMarksHotOnesForDeepScan()
    {
        var hits = new List<TopicHit>
        {
            new("群A", 100, "老王", "校招来了", "校招"),
            new("群A", 101, "小李", "内推吗", "内推"),
            new("群A", 102, "老王", "投了", "投递"),
            new("群B", 103, "小张", "招聘", "招聘"),
            new("群B", 104, "小张", "秋招", "秋招"),
            new("群C", 105, "某人", "offer", "offer"),
        };

        var queue = TopicPriorityQueue.Rank(hits);

        Assert.Equal(3, queue.Count);
        Assert.Equal("群A", queue[0].SessionId);
        Assert.Equal(3, queue[0].Hits);
        Assert.True(queue[0].DeepScan);
        Assert.True(queue[1].DeepScan);                       // 群B 命中 2 条 → 够格深扫
        Assert.False(queue[2].DeepScan);                      // 群C 只有 1 条 → 少扫（不深读）
        Assert.Equal(2, queue.Count(r => r.DeepScan));
    }

    [Fact]
    public void Rank_CapsDeepScanCount()
    {
        var hits = Enumerable.Range(0, 6)
            .Select(i => new TopicHit($"群{i}", 100 + i, "某人", "就业", "就业"))
            .SelectMany(h => Enumerable.Repeat(h, 3))         // 每个会话 3 条
            .ToList();

        var queue = TopicPriorityQueue.Rank(hits, deepCap: 2);

        Assert.Equal(2, queue.Count(r => r.DeepScan));         // 再多热点也只深扫 2 个
    }

    /// <summary>历史分只用于同分排序：老聊这个主题的群先深扫。</summary>
    [Fact]
    public void Rank_UsesHistoryAsTieBreak()
    {
        var hits = new List<TopicHit>
        {
            new("新群", 100, "某人", "校招", "校招"),
            new("老群", 101, "某人", "校招", "校招"),
        };
        var history = new Dictionary<string, int> { ["老群"] = 9 };

        var queue = TopicPriorityQueue.Rank(hits, history);

        Assert.Equal("老群", queue[0].SessionId);
        Assert.Equal(10, queue[0].Score);                     // 1 + 9
        Assert.Equal(1, queue[1].Score);
    }

    // ---------- 画像（"自主"的部分） ----------

    [Fact]
    public void Profile_RoundTrips_AndLearns()
    {
        var p = new TopicWatchProfile { Topic = "就业信息", Keywords = ["校招", "内推"] };
        p.Learn([new TopicRank("群A", 3, 3, true), new TopicRank("群B", 1, 1, false)]);

        var reloaded = TopicWatchProfile.Parse(p.ToJson());

        Assert.Equal("就业信息", reloaded.Topic);
        Assert.Equal(["校招", "内推"], reloaded.Keywords);
        Assert.Equal(3, reloaded.SessionHits["群A"]);

        // 第二轮再学一次 → 分数累计（"这个群常聊就业"是自己攒出来的）
        reloaded.Learn([new TopicRank("群A", 2, 2, true)]);
        Assert.Equal(5, reloaded.SessionHits["群A"]);
    }

    [Fact]
    public void Profile_BadJson_IsTreatedAsEmpty()
    {
        var p = TopicWatchProfile.Parse("{ 这不是 json");

        Assert.Empty(p.Keywords);
        Assert.Empty(p.SessionHits);
    }

    [Fact]
    public void Profile_NeedsKeywords_RespectsTtl()
    {
        var now = DateTimeOffset.Now;

        var fresh = new TopicWatchProfile { Keywords = ["校招"], ExpandedAt = now.AddHours(-1) };
        var stale = new TopicWatchProfile { Keywords = ["校招"], ExpandedAt = now.AddHours(-30) };
        var empty = new TopicWatchProfile();

        Assert.False(fresh.NeedsKeywords(now, TimeSpan.FromHours(12)));
        Assert.True(stale.NeedsKeywords(now, TimeSpan.FromHours(12)));     // 相关词会过季（秋招→春招）
        Assert.True(empty.NeedsKeywords(now, TimeSpan.FromHours(12)));
    }

    [Fact]
    public void Profile_Learn_KeepsOnlyTopSessions()
    {
        var p = new TopicWatchProfile();
        p.Learn(Enumerable.Range(0, 40).Select(i => new TopicRank($"群{i}", i, i, false)));

        Assert.Equal(30, p.SessionHits.Count);                            // 字典不无限增长
        Assert.True(p.SessionHits.ContainsKey("群39"));                    // 高分保留
        Assert.False(p.SessionHits.ContainsKey("群0"));
    }
}
