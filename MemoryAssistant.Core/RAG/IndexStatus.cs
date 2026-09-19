using System.Text.Json;

namespace MemoryAssistant.Core.RAG;

/// <summary>索引状态（rag_index_status 解析结果）。</summary>
public sealed record IndexStatus(
    bool Loaded,
    int Chunks,
    int Dim,
    int Sessions,
    int TotalMsgs,
    string? BuiltAt,
    string? LastUpdated,
    string? Scope,
    string? Path,
    string? Error);

/// <summary>解析 rag_index_status 响应（纯函数，可单测）。</summary>
public static class IndexStatusParser
{
    public static IndexStatus Parse(JsonElement je)
    {
        if (je.ValueKind != JsonValueKind.Object)
            return new IndexStatus(false, 0, 0, 0, 0, null, null, null, null, "invalid data");

        var error = GetString(je, "error");
        return new IndexStatus(
            GetBool(je, "loaded"),
            GetInt(je, "chunks"),
            GetInt(je, "dim"),
            GetInt(je, "sessions"),
            GetInt(je, "total_msgs"),
            GetString(je, "built_at"),
            GetString(je, "last_updated"),
            GetString(je, "scope"),
            GetString(je, "path"),
            error);
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
