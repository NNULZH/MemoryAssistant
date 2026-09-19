using MemoryAssistant.Core.Agent.Query;

namespace MemoryAssistant.Core.Agent.Skills;

/// <summary>检索时间窗（Unix 秒，闭区间；0 表示不限）。由 QueryModel 的 TimeKind 解析而来。</summary>
public sealed record SearchWindow(long Begin, long End)
{
    public static readonly SearchWindow Unbounded = new(0, 0);
    public bool IsBounded => Begin > 0 || End > 0;
}

/// <summary>把 QueryModel 的时间线索解析为可用的检索时间窗（V3.3：让"最近/上周/去年"真正生效）。</summary>
public static class TimeWindowResolver
{
    private static readonly TimeSpan Local = TimeSpan.FromHours(8);

    public static SearchWindow Resolve(QueryModel? model, DateTimeOffset? now = null)
    {
        if (model is null || model.TimeKind == TimeKind.None) return SearchWindow.Unbounded;
        var t = now ?? DateTimeOffset.Now;
        var localDate = t.ToOffset(Local);
        long BeginOfDay(DateTimeOffset d) => new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, Local).ToUnixTimeSeconds();
        var dayStart = BeginOfDay(localDate);
        long end = dayStart + 86400;

        return model.TimeKind switch
        {
            TimeKind.Today => new SearchWindow(dayStart, end),
            TimeKind.ThisWeek => new SearchWindow(dayStart - (int)localDate.DayOfWeek * 86400, end),
            TimeKind.LastWeek => new SearchWindow(dayStart - ((int)localDate.DayOfWeek + 7) * 86400, dayStart - (int)localDate.DayOfWeek * 86400),
            TimeKind.ThisMonth => new SearchWindow(BeginOfDay(new DateTimeOffset(localDate.Year, localDate.Month, 1, 0, 0, 0, Local)), end),
            TimeKind.LastMonth => LastMonth(localDate),
            TimeKind.ThisYear => new SearchWindow(BeginOfDay(new DateTimeOffset(localDate.Year, 1, 1, 0, 0, 0, Local)), end),
            TimeKind.LastYear => new SearchWindow(
                BeginOfDay(new DateTimeOffset(localDate.Year - 1, 1, 1, 0, 0, 0, Local)),
                BeginOfDay(new DateTimeOffset(localDate.Year, 1, 1, 0, 0, 0, Local))),
            TimeKind.RangeDays => RangeFromPhrase(model.TimePhrase, localDate),
            TimeKind.Recent => new SearchWindow(dayStart - 6 * 86400, end),   // 最近 ≈ 近 7 天
            TimeKind.SpecificDate => SpecificDate(model.TimePhrase),
            _ => SearchWindow.Unbounded,
        };
    }

    /// <summary>chunk/证据的日期字符串（yyyy-MM-dd）是否在窗口内。</summary>
    public static bool ContainsDate(SearchWindow w, string? date)
    {
        if (!w.IsBounded) return true;
        if (string.IsNullOrWhiteSpace(date) || date.Length < 10) return true; // 无日期信息的条目不因此被丢弃
        if (!DateTimeOffset.TryParseExact(date[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d)) return true;
        var sec = new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, Local).ToUnixTimeSeconds();
        if (w.Begin > 0 && sec < w.Begin) return false;
        if (w.End > 0 && sec >= w.End) return false;
        return true;
    }

    private static SearchWindow LastMonth(DateTimeOffset d)
    {
        var firstThisMonth = new DateTimeOffset(d.Year, d.Month, 1, 0, 0, 0, Local);
        var firstLastMonth = firstThisMonth.AddMonths(-1);
        return new SearchWindow(firstLastMonth.ToUnixTimeSeconds(), firstThisMonth.ToUnixTimeSeconds());
    }

    private static SearchWindow RangeFromPhrase(string phrase, DateTimeOffset now)
    {
        var m = System.Text.RegularExpressions.Regex.Match(phrase ?? "", @"([1-9]\d{0,2})\s*(天|周|个月|星期|年)");
        if (!m.Success) return new SearchWindow(now.AddDays(-7).ToUnixTimeSeconds(), now.AddDays(1).ToUnixTimeSeconds());
        var n = int.Parse(m.Groups[1].Value);
        var unit = m.Groups[2].Value;
        var start = unit switch
        {
            "天" => now.AddDays(-n),
            "周" or "星期" => now.AddDays(-7 * n),
            "个月" => now.AddMonths(-n),
            "年" => now.AddYears(-n),
            _ => now.AddDays(-n),
        };
        return new SearchWindow(start.ToUnixTimeSeconds(), now.AddDays(1).ToUnixTimeSeconds());
    }

    private static SearchWindow SpecificDate(string phrase)
    {
        if (!Query.DatePhrase.TryFind(phrase, out var d, out _)) return SearchWindow.Unbounded;
        var start = new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, Local);
        return new SearchWindow(start.ToUnixTimeSeconds(), start.AddDays(1).ToUnixTimeSeconds());
    }

    /// <summary>把窗口按天展开（yyyy-MM-dd，升序）；用于"按天取记录"这类精确检索。</summary>
    public static IReadOnlyList<string> Days(SearchWindow w, int maxDays = 3)
    {
        var days = new List<string>();
        if (!w.IsBounded) return days;
        var cur = DateTimeOffset.FromUnixTimeSeconds(w.Begin).ToOffset(Local).Date;
        var end = DateTimeOffset.FromUnixTimeSeconds(w.End).ToOffset(Local).Date;
        while (cur < end && days.Count < maxDays)
        {
            days.Add(cur.ToString("yyyy-MM-dd"));
            cur = cur.AddDays(1);
        }
        return days;
    }
}
