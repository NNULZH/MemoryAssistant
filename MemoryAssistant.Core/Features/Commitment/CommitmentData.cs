using System.Text.Json;

namespace MemoryAssistant.Core.Features.Commitment;

/// <summary>一条承诺候选卡片（规则初筛，待确认）。</summary>
public sealed record CommitmentCandidate(
    string SessionId,
    string SessionName,
    string Date,
    long CreateTime,
    string Sender,
    bool IsSelf,
    string Content,
    string MatchedText);

/// <summary>解析 rag_commitments 响应的 "candidates" 数组（纯函数，可单测）。</summary>
public static class CommitmentDataParser
{
    public static IReadOnlyList<CommitmentCandidate> Parse(JsonElement candidates)
    {
        if (candidates.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<CommitmentCandidate>();
        foreach (var c in candidates.EnumerateArray())
        {
            var sessionId = GetString(c, "session_id") ?? "";
            list.Add(new CommitmentCandidate(
                sessionId,
                GetString(c, "session_name") ?? sessionId,
                GetString(c, "date") ?? "",
                GetLong(c, "create_time"),
                GetString(c, "sender") ?? "",
                GetBool(c, "is_self"),
                GetString(c, "content") ?? "",
                GetString(c, "matched_text") ?? ""));
        }
        return list;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
