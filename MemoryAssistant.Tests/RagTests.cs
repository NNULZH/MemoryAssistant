using System.Text.Json;
using MemoryAssistant.Core.RAG;
using MemoryAssistant.Infrastructure.RAG;

namespace MemoryAssistant.Tests;

public class RagTests
{
    [Fact]
    public void ParseChunk_ExtractsFields()
    {
        var json = """
        {"session_id":"s1","session_name":"测试群","date":"2026-09-01",
         "text":"[10:00] A: 你好","msg_count":1,"start_time":1756700000,"end_time":1756700060}
        """;
        using var doc = JsonDocument.Parse(json);
        var chunk = BridgeRagRetriever.ParseChunk(doc.RootElement);

        Assert.NotNull(chunk);
        Assert.Equal("s1", chunk!.SessionId);
        Assert.Equal("测试群", chunk.SessionName);
        Assert.Equal("2026-09-01", chunk.Date);
        Assert.Equal("[10:00] A: 你好", chunk.Text);
        Assert.Equal(1, chunk.MsgCount);
    }

    [Fact]
    public void ParseChunk_MissingName_FallsBackToSessionId()
    {
        var json = """{"session_id":"wxid_x","date":"2026-09-01","text":"x","msg_count":1}""";
        using var doc = JsonDocument.Parse(json);
        var chunk = BridgeRagRetriever.ParseChunk(doc.RootElement);
        Assert.Equal("wxid_x", chunk!.SessionName);
    }

    private sealed class StubRetriever : IRetriever
    {
        private readonly RetrievedChunk[] _results;
        public StubRetriever(params RetrievedChunk[] results) => _results = results;
        public string Name => "stub";
        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string q, int topK, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetrievedChunk>>(_results.Take(topK).ToList());
    }

    [Fact]
    public async Task HybridRetriever_MergesVectorAndFts()
    {
        var vector = new StubRetriever(
            new RetrievedChunk { SessionId = "a", Date = "2026-09-01", Text = "A", Source = "vector" },
            new RetrievedChunk { SessionId = "b", Date = "2026-09-01", Text = "B", Source = "vector" });
        var fts = new StubRetriever(
            new RetrievedChunk { SessionId = "c", Date = "2026-09-01", Text = "C", Source = "fts" },
            new RetrievedChunk { SessionId = "a", Date = "2026-09-01", Text = "A", Source = "fts" });

        var hybrid = new HybridRetriever(vector, fts);
        var results = await hybrid.SearchAsync("q", 5);

        // a 同时被 vector+fts 命中，应排第一
        Assert.Equal("a", results[0].SessionId);
        Assert.Equal("hybrid", results[0].Source);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task HybridRetriever_EmptyFts_ReturnsVectorOnly()
    {
        var vector = new StubRetriever(
            new RetrievedChunk { SessionId = "a", Date = "2026-09-01", Text = "A", Source = "vector" });
        var fts = new StubRetriever();

        var hybrid = new HybridRetriever(vector, fts);
        var results = await hybrid.SearchAsync("q", 5);
        Assert.Single(results);
        Assert.Equal("a", results[0].SessionId);
    }
}
