namespace MemoryAssistant.Core.Missions;

/// <summary>
/// 追踪/回复对象的别名映射：这些会话在本地库里存的是英文或固定 id，
/// 用户口语里说的名字按原样检索命中不了（"文件传输助手"其实叫 filehelper）。
/// 探测（增量判断）与自动回复（发消息前的会话解析）必须用同一份映射，否则会出现
/// "探测得到、回复发不出"这种撕裂行为。
/// </summary>
public static class MissionTargetAliases
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["文件传输助手"] = "filehelper",
        ["微信团队"] = "weixin",
    };

    /// <summary>口语名 → 库里可检索的 id（未收录时原样返回）。</summary>
    public static string Resolve(string target)
    {
        var t = (target ?? "").Trim();
        return Map.TryGetValue(t, out var alias) ? alias : t;
    }
}
