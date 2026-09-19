using System.Text.Json;

namespace MemoryAssistant.Core.Agent;

/// <summary>
/// 把工具返回的原始 JSON 转成"可引用的证据"（Evidence）。
///
/// 为什么需要：tools Skill 让模型自主调工具，但工具输出原来只进模型上下文、从不进 EvidenceStore，
/// 于是答案没有 [N] 引用、UI 显示"未读取聊天记录"（其实读了一大堆）、也没法点证据跳转。
///
/// 只提取"能定位到具体消息/片段"的工具结果；统计（get_session_stats）、会话列表（list_sessions）
/// 这类聚合结果是**结论**而不是原文，不当证据（它们走 Notes）。
/// 输出被截断/不是 JSON 时诚实返回空，不影响主流程。
/// </summary>
public static class ToolEvidenceExtractor
{
    /// <summary>单次工具调用最多提取多少条（控 token 与 UI 体积）。</summary>
    public const int MaxPerCall = 12;

    /// <summary>这些内容只是占位符（图片/表情包等），当证据没有信息量。</summary>
    private static readonly string[] Placeholders =
        ["[图片]", "[表情包]", "[语音]", "[视频]", "[动画表情]", "[文件]", "[位置]", "[链接]"];

    public static IReadOnlyList<Evidence> Extract(string toolName, string? argsJson, string? outputJson)
    {
        var json = RepairTruncated(outputJson);
        if (json.Length == 0) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            return toolName switch
            {
                "search_messages" => FromMessages(doc.RootElement, argsJson),
                // read_messages 的返回里没有 session_id（由请求参数限定），从参数兜底取回
                "read_messages" => FromMessages(doc.RootElement, argsJson),
                "retrieve_memory" => FromChunks(doc.RootElement),
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>工具输出超长时的截断标记（与 BridgeToolProvider 保持一致）。</summary>
    private const string OmittedMarker = "...[截断]";

    /// <summary>
    /// 容错：工具输出超过 MaxToolResultChars 会被截断并追加"...[截断]"，JSON 就不再合法——
    /// 而 read_messages/limit=100 这种调用几乎必然超长。做法是把数组截到"最后一个完整元素"再补 ']'，
    /// 已经拿到的那部分照样能当证据用（否则工具"查了但等于没查"，答案永远没有引用）。
    /// </summary>
    private static string RepairTruncated(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Trim();
        if (text.EndsWith(OmittedMarker, StringComparison.Ordinal))
            text = text[..^OmittedMarker.Length].TrimEnd();

        if (IsJson(text)) return text;
        if (text.Length == 0 || text[0] != '[') return "";

        // 扫到"深度回到 1 的那个 '}'"——它就是最后一个完整元素
        int depth = 0, lastGoodEnd = -1;
        bool inStr = false, esc = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            switch (ch)
            {
                case '"': inStr = true; break;
                case '[' or '{': depth++; break;
                case '}' or ']':
                    depth--;
                    if (depth == 1 && ch == '}') lastGoodEnd = i;
                    break;
            }
        }

        if (lastGoodEnd < 0) return "";
        var repaired = text[..(lastGoodEnd + 1)] + "]";
        return IsJson(repaired) ? repaired : "";
    }

    private static bool IsJson(string text)
    {
        if (text.Length == 0) return false;
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>search_messages / read_messages：数组里每条就是一条消息。</summary>
    private static List<Evidence> FromMessages(JsonElement array, string? argsJson)
    {
        var fallbackSession = ArgString(argsJson, "session_id");
        var list = new List<Evidence>();
        foreach (var m in array.EnumerateArray())
        {
            if (list.Count >= MaxPerCall) break;
            if (m.ValueKind != JsonValueKind.Object) continue;
            var content = Str(m, "content");
            if (!IsCitable(content)) continue;

            var sessionId = Str(m, "session_id");
            var isSelf = m.TryGetProperty("is_self", out var self) && self.ValueKind == JsonValueKind.True;

            list.Add(new Evidence
            {
                SessionId = sessionId is { Length: > 0 } ? sessionId : fallbackSession,
                // 会话名用单独字段（私聊=对方名、群聊=群名）；display_name 是**发言者**，
                // 私聊里你自己发的那条它会是你自己的名字，拿它当会话名会张冠李戴。
                SessionDisplayName = Str(m, "session_name") ?? "",
                SenderName = isSelf ? "我" : Str(m, "display_name") ?? "",
                CreateTime = Long(m, "create_time"),
                Content = content!,
                Source = "chat",
            });
        }
        return list;
    }

    /// <summary>retrieve_memory（RAG 语义检索）：每项形如 { "chunk": { session_id, session_name, date, text } }。</summary>
    private static List<Evidence> FromChunks(JsonElement array)
    {
        var list = new List<Evidence>();
        foreach (var item in array.EnumerateArray())
        {
            if (list.Count >= MaxPerCall) break;
            if (!item.TryGetProperty("chunk", out var c) || c.ValueKind != JsonValueKind.Object) continue;
            var text = Str(c, "text");
            if (!IsCitable(text)) continue;

            list.Add(new Evidence
            {
                SessionId = Str(c, "session_id") ?? "",
                SessionDisplayName = Str(c, "session_name") ?? "",
                Date = Str(c, "date") ?? "",
                Content = text!,
                Source = "rag",
            });
        }
        return list;
    }

    private static bool IsCitable(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        return !Placeholders.Contains(content.Trim(), StringComparer.Ordinal);
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0L;

    private static string ArgString(string? argsJson, string name)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return Str(doc.RootElement, name) ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
