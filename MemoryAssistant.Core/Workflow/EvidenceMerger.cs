using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Features.Commitment;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Core.Workflow;

/// <summary>
/// 证据整合：把 RAG 片段 / FTS 片段 / 工具读取的消息原文
/// 统一为 Evidence 列表，去重、排序、编号（[1][2]...）。
/// </summary>
public static class EvidenceMerger
{
    /// <summary>把检索片段转为 Evidence 并编号。source: rag | fts。</summary>
    public static IReadOnlyList<Evidence> FromChunks(IReadOnlyList<RetrievedChunk> chunks, string source)
    {
        var list = new List<Evidence>();
        var seen = new HashSet<string>();
        foreach (var c in chunks)
        {
            var key = $"{c.SessionId}|{c.Date}";
            if (!seen.Add(key)) continue;
            list.Add(new Evidence
            {
                Index = list.Count + 1,
                SessionId = c.SessionId,
                SessionDisplayName = c.SessionName,
                CreateTime = TryParseDate(c.Date),
                SenderName = "",
                Content = Truncate(c.Text, 2000),
                Source = source,
            });
        }
        return list;
    }

    /// <summary>把工具读取的原始消息转成 Evidence（精读阶段）。</summary>
    public static IReadOnlyList<Evidence> FromToolMessages(
        IEnumerable<ToolMessageInput> messages,
        string sessionName,
        int maxPerSession = 50)
    {
        var list = new List<Evidence>();
        foreach (var m in messages.Take(maxPerSession))
        {
            list.Add(new Evidence
            {
                Index = list.Count + 1,
                SessionId = m.SessionId,
                SessionDisplayName = sessionName,
                CreateTime = m.CreateTime,
                SenderName = m.SenderName,
                Content = Truncate(m.Content, 500),
                Source = "chat",
            });
        }
        return list;
    }

    /// <summary>把承诺候选转成 Evidence（Source="commitment"），去重编号。</summary>
    public static IReadOnlyList<Evidence> FromCommitmentCandidates(
        IEnumerable<CommitmentCandidate> candidates,
        int max = 100)
    {
        var list = new List<Evidence>();
        var seen = new HashSet<string>();
        foreach (var c in candidates)
        {
            var key = $"{c.SessionId}|{c.CreateTime}|{c.Sender}|{c.Content.GetHashCode()}";
            if (!seen.Add(key)) continue;
            list.Add(new Evidence
            {
                Index = list.Count + 1,
                SessionId = c.SessionId,
                SessionDisplayName = c.SessionName,
                CreateTime = c.CreateTime,
                SenderName = c.Sender,
                Content = Truncate(c.Content, 500),
                Source = "commitment",
            });
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>合并多组证据，去重（会话+时间 相同视为重复），重编号。</summary>
    public static IReadOnlyList<Evidence> Merge(params IReadOnlyList<Evidence>[] groups)
    {
        var list = new List<Evidence>();
        var seen = new HashSet<string>();
        foreach (var group in groups)
        {
            foreach (var e in group)
            {
                var key = $"{e.SessionId}|{e.CreateTime}|{e.SenderName}|{e.Content.GetHashCode()}";
                if (!seen.Add(key)) continue;
                list.Add(e with { Index = list.Count + 1 });
            }
        }
        return list;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    private static long TryParseDate(string date)
    {
        if (DateTime.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var dt))
            return new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds();
        return 0;
    }
}

/// <summary>工具读取到的单条消息（Evidence 构建输入）。</summary>
public sealed record ToolMessageInput(
    string SessionId,
    string SenderName,
    string Content,
    long CreateTime);
