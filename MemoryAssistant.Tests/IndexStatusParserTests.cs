using System.Text.Json;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Tests;

public class IndexStatusParserTests
{
    private static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parse_ExtractsFields()
    {
        var je = Doc("""
        {"loaded":true,"chunks":8127,"dim":512,"sessions":135,"total_msgs":69100,
         "built_at":"2026-09-08T12:00:00+08:00","last_updated":"2026-09-08T20:00:00+08:00",
         "scope":"all","path":"D:\\agent\\data\\index\\chunks.json"}
        """);
        var s = IndexStatusParser.Parse(je);
        Assert.True(s.Loaded);
        Assert.Equal(8127, s.Chunks);
        Assert.Equal(512, s.Dim);
        Assert.Equal(135, s.Sessions);
        Assert.Equal(69100, s.TotalMsgs);
        Assert.Equal("2026-09-08T12:00:00+08:00", s.BuiltAt);
        Assert.Equal("all", s.Scope);
        Assert.Null(s.Error);
    }

    [Fact]
    public void Parse_UnloadedAndMissingFields_Defaults()
    {
        var je = Doc("""{"loaded":false}""");
        var s = IndexStatusParser.Parse(je);
        Assert.False(s.Loaded);
        Assert.Equal(0, s.Chunks);
        Assert.Null(s.BuiltAt);
    }

    [Fact]
    public void Parse_NonObject_ReturnsError()
    {
        var je = Doc("""["x"]""");
        var s = IndexStatusParser.Parse(je);
        Assert.Equal("invalid data", s.Error);
        Assert.False(s.Loaded);
    }

    [Fact]
    public void Parse_ErrorField_Passthrough()
    {
        var je = Doc("""{"error":"boom","loaded":false}""");
        var s = IndexStatusParser.Parse(je);
        Assert.Equal("boom", s.Error);
    }

    [Fact]
    public void Parse_EmptyObject_AllDefaults()
    {
        var s = IndexStatusParser.Parse(Doc("""{}"""));
        Assert.False(s.Loaded);
        Assert.Equal(0, s.Chunks);
        Assert.Null(s.BuiltAt);
        Assert.Null(s.Error);
    }
}
