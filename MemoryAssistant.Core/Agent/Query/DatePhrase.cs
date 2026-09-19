using System.Text.RegularExpressions;

namespace MemoryAssistant.Core.Agent.Query;

/// <summary>
/// 中文口语日期识别（"9月11日/9月11号/9月11/2026年9月11日/9-11/9/11"）。
/// 单独抽出来是因为下游有两个消费者（目标抽取与检索时间窗），
/// 两边规则不一致就会出现"用户说了日期、系统当没看见"的情况。
/// </summary>
public static class DatePhrase
{
    private static readonly Regex Pattern = new(
        @"(?:(?<y>\d{4})\s*年\s*)?(?<m>\d{1,2})\s*月\s*(?<d>\d{1,2})\s*[日号]?" +
        @"|(?<m2>\d{1,2})\s*[/\-．.]\s*(?<d2>\d{1,2})(?!\d)",
        RegexOptions.Compiled);

    /// <summary>在文本里找出第一个合理日期；找不到返回 false。</summary>
    public static bool TryFind(string? text, out DateTime date, out string phrase)
    {
        date = default;
        phrase = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        var m = Pattern.Match(text);
        if (!m.Success) return false;

        var hasYear = m.Groups["y"].Success;
        var month = hasYear || m.Groups["m"].Success ? Parse(m, "m") : Parse(m, "m2");
        var day = hasYear || m.Groups["m"].Success ? Parse(m, "d") : Parse(m, "d2");
        if (month is < 1 or > 12 || day is < 1 or > 31) return false;

        var year = hasYear ? int.Parse(m.Groups["y"].Value) : DateTimeOffset.Now.Year;
        try
        {
            date = new DateTime(year, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;   // 例如 2 月 30 日
        }
        phrase = m.Value.Trim();
        return true;
    }

    private static int Parse(Match m, string group)
        => m.Groups[group].Success ? int.Parse(m.Groups[group].Value) : 0;
}
