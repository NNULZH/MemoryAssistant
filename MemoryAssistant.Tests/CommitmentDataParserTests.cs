using System.Text.Json;
using MemoryAssistant.Core.Features.Commitment;

namespace MemoryAssistant.Tests;

public class CommitmentDataParserTests
{
    private static JsonElement Arr(params string[] json)
        => JsonDocument.Parse($"[{string.Join(",", json)}]").RootElement;

    [Fact]
    public void Parse_ValidCandidates_ExtractsFields()
    {
        var arr = Arr(
            """{"session_id":"s1","session_name":"群A","date":"2026-08-11","create_time":1756800000,"sender":"我","is_self":true,"content":"到时候再看","matched_text":"到时候再"}""",
            """{"session_id":"s2","session_name":"B","date":"2026-08-16","create_time":0,"sender":"xyc","is_self":false,"content":"我明天看看","matched_text":"我明天"}""");

        var list = CommitmentDataParser.Parse(arr);

        Assert.Equal(2, list.Count);
        Assert.Equal("群A", list[0].SessionName);
        Assert.True(list[0].IsSelf);
        Assert.Equal("到时候再", list[0].MatchedText);
        Assert.Equal(1756800000L, list[0].CreateTime);
        Assert.False(list[1].IsSelf);
        Assert.Equal("xyc", list[1].Sender);
    }

    [Fact]
    public void Parse_MissingFields_FallsBackToSessionId()
    {
        var arr = Arr("""{"session_id":"s1","content":"我发给你"}""");
        var list = CommitmentDataParser.Parse(arr);
        Assert.Single(list);
        Assert.Equal("s1", list[0].SessionName);
        Assert.False(list[0].IsSelf);
        Assert.Equal("", list[0].MatchedText);
    }

    [Fact]
    public void Parse_EmptyArray_ReturnsEmpty()
    {
        Assert.Empty(CommitmentDataParser.Parse(Arr()));
    }

    [Fact]
    public void Parse_NonArray_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"candidates":123}""");
        Assert.True(doc.RootElement.TryGetProperty("candidates", out var v));
        Assert.Empty(CommitmentDataParser.Parse(v));
    }
}
