using System.Text.Json;

namespace MemoryAssistant.Core.Agent.Planner;

/// <summary>
/// 把 LLM 输出的计划文本解析为 AgentPlan（纯函数，可测）。
/// 兼容：
///  - 裸 JSON
///  - ```json ... ``` / ``` ... ``` 围栏包裹
///  - 前后有多余说明文字（取第一段合法 JSON 对象）
/// 失败返回 null（由上层回退到规则计划）。
/// </summary>
public static class PlanJsonParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static AgentPlan? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var candidate in CandidateBlocks(text))
        {
            if (TryParseObject(candidate, out var plan)) return plan;
        }
        return null;
    }

    /// <summary>把原始文本切成"可能是 JSON 对象"的候选块（去围栏/去说明文字）。</summary>
    private static IEnumerable<string> CandidateBlocks(string text)
    {
        var trimmed = text.Trim();

        // 直接就是 {...}（含首尾换行）
        if (LooksLikeObject(trimmed)) { yield return trimmed; yield break; }

        // 围栏代码块
        var lines = trimmed.Split('\n');
        var inFence = false;
        var buffer = new System.Text.StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    if (buffer.Length > 0) yield return buffer.ToString();
                    buffer.Clear();
                    inFence = false;
                }
                else inFence = true;
                continue;
            }
            if (inFence) buffer.AppendLine(line);
        }
        if (inFence && buffer.Length > 0) { yield return buffer.ToString(); yield break; }

        // 说明文字包裹的裸 JSON：按大括号配对逐个扫描候选对象
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] != '{') continue;
            int depth = 0;
            bool inString = false;
            for (int j = i; j < trimmed.Length; j++)
            {
                var c = trimmed[j];
                if (c == '"' && (j == 0 || trimmed[j - 1] != '\\')) inString = !inString;
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    yield return trimmed[i..(j + 1)];
                    i = j; // 跳过该对象内部，继续找下一个候选
                    break;
                }
            }
        }
    }

    private static bool LooksLikeObject(string s)
    {
        var t = s.Trim();
        return t.StartsWith('{') && t.EndsWith('}');
    }

    private static bool TryParseObject(string json, out AgentPlan plan)
    {
        plan = null!;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            var root = doc.RootElement;
            var goal = root.TryGetProperty("goal", out var g) ? g.GetString() ?? "" : "";
            var stop = root.TryGetProperty("stop_condition", out var sc) ? sc.GetString() : null;
            var steps = new List<PlanStep>();
            if (root.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = item.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    var kind = item.TryGetProperty("type", out var k)
                        ? k.GetString() ?? PlanStepKind.Skill
                        : item.TryGetProperty("kind", out var k2) ? k2.GetString() ?? PlanStepKind.Skill : PlanStepKind.Skill;
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var reason = item.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    steps.Add(new PlanStep { Id = id, Kind = kind, Name = name, Reason = reason });
                }
            }
            plan = new AgentPlan { Goal = goal, Steps = steps, StopCondition = stop };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
