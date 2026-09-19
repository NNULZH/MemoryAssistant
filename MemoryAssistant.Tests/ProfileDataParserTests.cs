using System.Text.Json;
using MemoryAssistant.Core.Features.Profiles;

namespace MemoryAssistant.Tests;

public class ProfileDataParserTests
{
    private static JsonElement Arr(params string[] json)
        => JsonDocument.Parse($"[{string.Join(",", json)}]").RootElement;

    [Fact]
    public void Parse_ExtractsFields()
    {
        var arr = Arr(
            """{"session_id":"s1","session_name":"群A","msg_count":10000,"day_count":266,"first_date":"2025-07-05","last_date":"2026-09-07","top_keyword_topics":[{"category":"游戏","count":100}]}""");

        var list = ProfileDataParser.Parse(arr);

        Assert.Single(list);
        var p = list[0];
        Assert.Equal("群A", p.SessionName);
        Assert.Equal(10000, p.MsgCount);
        Assert.Equal(266, p.DayCount);
        Assert.Equal("2025-07-05", p.FirstDate);
        Assert.Single(p.TopKeywordTopics);
        Assert.Equal("游戏", p.TopKeywordTopics[0].Category);
        Assert.Equal(100, p.TopKeywordTopics[0].Total);
    }

    [Fact]
    public void Parse_MissingFields_Tolerant()
    {
        var arr = Arr("""{"session_id":"s1"}""");
        var list = ProfileDataParser.Parse(arr);
        Assert.Single(list);
        Assert.Equal("s1", list[0].SessionName);
        Assert.Equal(0, list[0].MsgCount);
        Assert.Empty(list[0].TopKeywordTopics);
    }

    [Fact]
    public void Parse_EmptyArray_ReturnsEmpty()
    {
        Assert.Empty(ProfileDataParser.Parse(Arr()));
    }

    [Fact]
    public void HourlyHistogram_Compute_BucketsByLocalHour()
    {
        // 今天 0..23 点各一条
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var todayStart = now - (now % 86400);
        var times = Enumerable.Range(0, 24).Select(h => todayStart + h * 3600L).ToList();

        var hist = HourlyHistogram.Compute(times);

        Assert.Equal(24, hist.Length);
        Assert.All(hist, v => Assert.Equal(1, v));
    }

    [Fact]
    public void HourlyHistogram_TopHours_RanksTopN()
    {
        var hist = new int[24];
        hist[22] = 30; // 22-23 点最多
        hist[23] = 20;
        hist[0] = 10;
        hist[12] = 5;

        var top = HourlyHistogram.TopHours(hist, 3);

        Assert.Equal(3, top.Count);
        Assert.Equal((22, 30), top[0]);
        Assert.Equal((23, 20), top[1]);
        Assert.Equal((0, 10), top[2]);
    }

    [Fact]
    public void HourlyHistogram_EmptyInput_AllZero()
    {
        var hist = HourlyHistogram.Compute([]);
        Assert.All(hist, v => Assert.Equal(0, v));
        Assert.Empty(HourlyHistogram.TopHours(hist));
    }
}
