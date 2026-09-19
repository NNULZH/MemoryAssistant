using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Features.Topics;

namespace MemoryAssistant.Tests;

public class TopicDataParserTests
{
    private static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parse_ParsesKeywordTopicsAndClusters()
    {
        var json = """
        {
          "chunks_in_scope": 310,
          "keyword_topics": [
            {"category":"实习工作","total":84,"per_day":[{"date":"2026-09-01","count":10}]}
          ],
          "clusters": [
            {"cluster_id":5,"size":70,"representative":"代表文本",
             "per_day":[{"date":"2026-09-02","count":3}],
             "samples":[{"session_id":"s1","session_name":"群A","date":"2026-09-02","text":"片段"}]}
          ]
        }
        """;
        var data = TopicDataParser.Parse(Doc(json));

        Assert.Equal(310, data.ChunksInScope);
        Assert.Single(data.KeywordTopics);
        Assert.Equal("实习工作", data.KeywordTopics[0].Category);
        Assert.Equal(84, data.KeywordTopics[0].Total);
        Assert.Equal(10, data.KeywordTopics[0].PerDay[0].Count);
        Assert.Single(data.Clusters);
        Assert.Equal(70, data.Clusters[0].Size);
        Assert.Equal("代表文本", data.Clusters[0].Representative);
        Assert.Equal("群A", data.Clusters[0].Samples[0].SessionName);
    }

    [Fact]
    public void Parse_MissingFields_Tolerant()
    {
        var json = """{"keyword_topics":[{"category":"课程"}],"clusters":[]}""";
        var data = TopicDataParser.Parse(Doc(json));
        Assert.Single(data.KeywordTopics);
        Assert.Equal(0, data.KeywordTopics[0].Total);
        Assert.Equal(0, data.ChunksInScope);
    }

    [Fact]
    public void Parse_NonObject_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""[]""");
        var data = TopicDataParser.Parse(doc.RootElement);
        Assert.Empty(data.KeywordTopics);
        Assert.Empty(data.Clusters);
    }

    [Fact]
    public void Merge_CombinesKeywordAndClusterTopics_SortedDesc()
    {
        var keywords = new[] { new KeywordTopic("游戏", 20, []) };
        var clusters = new[]
        {
            new TopicCluster(0, 50, "r0", [], []),
            new TopicCluster(1, 10, "r1", [], []),
        };
        var named = new[]
        {
            new TopicNamer.NamedCluster(0, "找工作"),
            new TopicNamer.NamedCluster(1, "考研"),
        };

        var merged = TopicMerger.Merge(keywords, named, clusters);

        Assert.Equal(3, merged.Count);
        Assert.Equal("找工作", merged[0].Label);
        Assert.Equal("聚类主题", merged[0].Kind);
        Assert.Equal(50, merged[0].TotalSize);
        Assert.Equal("游戏", merged[1].Label);
        Assert.Equal("关键词主题", merged[1].Kind);
        Assert.Equal("考研", merged[2].Label);
    }

    [Fact]
    public void Merge_UnnamedCluster_FallsBackToTopicN()
    {
        var clusters = new[] { new TopicCluster(3, 30, "r", [], []) };
        var merged = TopicMerger.Merge([], [], clusters);
        Assert.Single(merged);
        Assert.Equal("主题4", merged[0].Label); // cluster_id 3 → 主题4
    }

    [Fact]
    public async Task NameClustersAsync_ParsesJsonLabels()
    {
        var client = new ScriptedChatClient([new ChatResult { Content = """{"topics":[{"cluster_id":0,"label":"找工作"}]}""" }]);
        string? systemPrompt = null;
        client.CallsHooks = msgs => systemPrompt = msgs.FirstOrDefault(m => m.Role == "system")?.Content;
        var namer = new TopicNamer(client);
        var clusters = new[] { new TopicCluster(0, 50, "文本", [], []) };

        var named = await namer.NameClustersAsync(clusters);

        Assert.Single(named);
        Assert.Equal(0, named[0].ClusterId);
        Assert.Equal("找工作", named[0].Label);
        // system prompt 应包含 JSON 输出形状要求
        Assert.NotNull(systemPrompt);
        Assert.Contains("topics", systemPrompt);
    }

    [Fact]
    public async Task NameClustersAsync_Unparseable_FallsBackToTopicN()
    {
        var client = new ScriptedChatClient([new ChatResult { Content = "抱歉我不知道" }]);
        var namer = new TopicNamer(client);
        var clusters = new[] { new TopicCluster(0, 50, "文本", [], []) };

        var named = await namer.NameClustersAsync(clusters);

        Assert.Single(named);
        Assert.Equal("主题1", named[0].Label);
    }

    [Fact]
    public void ParseLabels_HandlesMarkdownCodeBlock()
    {
        var labels = TopicNamer.ParseLabels("```json\n{\"topics\":[{\"cluster_id\":2,\"label\":\"开黑\"}]}\n```");
        Assert.Single(labels);
        Assert.Equal(2, labels[0].ClusterId);
        Assert.Equal("开黑", labels[0].Label);
    }
}
