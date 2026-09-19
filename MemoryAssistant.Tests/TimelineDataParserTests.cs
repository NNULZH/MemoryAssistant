using System.Text.Json;
using MemoryAssistant.Core.Timeline;

namespace MemoryAssistant.Tests;

public class TimelineDataParserTests
{
    private static JsonElement Days(params string[] json)
    {
        var doc = JsonDocument.Parse($"[{string.Join(",", json)}]");
        return doc.RootElement;
    }

    [Fact]
    public void ParseDays_GroupsMonthsAndParsesDayFields()
    {
        var days = Days(
            """{"date":"2026-09-07","msg_count":2907,"session_count":29,"sessions":[{"session_id":"a","session_name":"群A","msg_count":2720}]}""",
            """{"date":"2026-08-31","msg_count":100,"session_count":3,"sessions":[]}""",
            """{"date":"2026-09-01","msg_count":50,"session_count":2,"sessions":[]}""");

        var data = TimelineDataParser.ParseDays(days);

        Assert.Equal(3, data.Days.Count);
        // 按日期降序
        Assert.Equal(["2026-09-07", "2026-09-01", "2026-08-31"], data.Days.Select(d => d.Date));
        // 月份分组：9 月、8 月（降序）
        Assert.Equal(["2026-09", "2026-08"], data.Months);
        // 字段解析
        var first = data.Days[0];
        Assert.Equal(2907, first.MsgCount);
        Assert.Equal(29, first.SessionCount);
        Assert.Single(first.Sessions);
        Assert.Equal("群A", first.Sessions[0].SessionName);
        Assert.Equal(2720, first.Sessions[0].MsgCount);
    }

    [Fact]
    public void ParseDays_SkipsInvalidDateAndMissingFields()
    {
        var days = Days(
            """{"date":"2026-09-07","msg_count":10,"session_count":1,"sessions":[]}""",
            """{"date":"bad"}""",
            """{"msg_count":5}""");

        var data = TimelineDataParser.ParseDays(days);

        Assert.Single(data.Days);
        Assert.Equal(10, data.Days[0].MsgCount);
        Assert.Equal(["2026-09"], data.Months);
    }

    [Fact]
    public void ParseDays_EmptyArrayReturnsEmptyData()
    {
        var data = TimelineDataParser.ParseDays(Days());
        Assert.Empty(data.Days);
        Assert.Empty(data.Months);
    }

    [Fact]
    public void ParseDays_NonArrayReturnsEmptyData()
    {
        using var doc = JsonDocument.Parse("""{"days":null}""");
        Assert.True(doc.RootElement.TryGetProperty("days", out var days));
        var data = TimelineDataParser.ParseDays(days);
        Assert.Empty(data.Days);
        Assert.Empty(data.Months);
    }

    [Fact]
    public void ParseDayChunks_ParsesChunkFields()
    {
        using var doc = JsonDocument.Parse(
            """[{"session_id":"a","session_name":"群A","text":"第一条","msg_count":5},{"session_id":"b","session_name":"B","text":"第二条","msg_count":1}]""");

        var chunks = TimelineDataParser.ParseDayChunks(doc.RootElement);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("群A", chunks[0].SessionName);
        Assert.Equal("第一条", chunks[0].Text);
        Assert.Equal(5, chunks[0].MsgCount);
        Assert.Equal("B", chunks[1].SessionName);
    }

    [Fact]
    public void ParseDayChunks_NonArrayReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"chunks":123}""");
        Assert.True(doc.RootElement.TryGetProperty("chunks", out var chunks));
        Assert.Empty(TimelineDataParser.ParseDayChunks(chunks));
    }
}
