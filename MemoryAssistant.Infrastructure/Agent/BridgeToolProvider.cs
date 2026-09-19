using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.PythonBridge;

namespace MemoryAssistant.Infrastructure.Agent;

/// <summary>
/// 把 Python Bridge 的 wxchat 方法封装为 C# ToolRegistry 工具。
/// 每个工具声明真实参数（ToolParameterSpec）→ ToolRegistry 生成完整 JSON Schema，
/// 执行时按 spec 填默认值 + Eco 钳制，输出超过 MaxToolResultChars 时截断。
/// </summary>
public sealed class BridgeToolProvider
{
    private readonly IPythonBridge _bridge;
    private readonly AgentOptions _agentOptions;
    private static readonly JsonSerializerOptions JsonOpts = new();

    public BridgeToolProvider(IPythonBridge bridge, AgentOptions agentOptions)
    {
        _bridge = bridge;
        _agentOptions = agentOptions;
    }

    private static ToolParameterSpec P(string name, string type = ToolParamType.String,
        string? desc = null, bool required = false, object? def = null,
        double? min = null, double? max = null, IReadOnlyList<string>? enumValues = null)
        => new()
        {
            Name = name,
            Type = type,
            Description = desc,
            Required = required,
            Default = def,
            Minimum = min,
            Maximum = max,
            Enum = enumValues,
        };

    /// <summary>构建全部 6 个 wxchat 工具（真实参数 schema）并注册进 registry。</summary>
    public void RegisterAll(ToolRegistry registry)
    {
        registry.Register(Tool(ToolCategory.Search, "list_sessions",
            "列出微信会话（找人/找群）。支持按关键词过滤。每项含 last_time_text（本地时间）、last_timestamp、unread_count。",
            P("limit", ToolParamType.Integer, "返回会话数上限", def: 20, min: 1, max: 100),
            P("keyword", ToolParamType.String, "按关键词过滤（群名/昵称）", def: "")));

        // 关键：list_sessions 的关键词只匹配 wxid / 会话摘要 / 最后发言人，问"我和某人的聊天"会命中
        // "那个人恰好发言过的群"。find_sessions 会额外匹配**微信备注名/昵称**并可按私聊/群过滤，
        // 是"按人名定位会话"的正确入口。（这个 bridge 方法早就写好了，但一直没在 C# 侧注册，模型调不到。）
        registry.Register(Tool(ToolCategory.Search, "find_sessions",
            "按人名/会话名找会话：匹配微信备注名与昵称，可选只要私聊。问\"我和某人的聊天记录\"必须用它，不要用 list_sessions 的关键词。",
            P("keyword", ToolParamType.String, "人名或会话名（匹配备注名/昵称/wxid）", def: ""),
            P("limit", ToolParamType.Integer, "返回会话数上限", def: 10, min: 1, max: 50),
            P("private_only", ToolParamType.Boolean, "true=只要私聊，false=只要群，不传=不限")));

        registry.Register(Tool(ToolCategory.Memory, "read_messages",
            "读取会话的消息原文，**直连数据库、拿到的是最新数据**。省略 begin/end 时按时间倒序返回最新的 limit 条"
            + "（问\"最新/今天\"就该这样用，且**更快**）。带 begin/end 会从最新往回翻页扫描，慢且容易超时，只在"
            + "明确要翻很久以前某一天时才用。"
            + "每行含：time_text(**本地时间，显示时间一律用它**)、create_time(Unix秒)、display_name(发言者)、"
            + "is_self(true=你自己发的)、session_name(该会话是谁：私聊=对方名，群聊=群名)。",
            P("session_id", ToolParamType.String, "会话 id（find_sessions / list_sessions 可得）", required: true),
            P("limit", ToolParamType.Integer, "读取条数上限", def: 30, min: 1, max: 200),
            P("offset", ToolParamType.Integer, "偏移量", def: 0, min: 0),
            P("begin", ToolParamType.Integer, "开始时间（Unix 秒，0=不限）", def: 0),
            P("end", ToolParamType.Integer, "结束时间（Unix 秒，0=不限）", def: 0)));

        registry.Register(Tool(ToolCategory.Search, "search_messages", "全文检索消息，返回含关键词的消息及会话/时间/发送者。",
            P("keyword", ToolParamType.String, "检索关键词", required: true),
            P("session_id", ToolParamType.String, "限定会话（可空=全部）", def: ""),
            P("limit", ToolParamType.Integer, "结果条数上限", def: 20, min: 1, max: 200),
            P("begin", ToolParamType.Integer, "开始时间（Unix 秒，0=不限）", def: 0),
            P("end", ToolParamType.Integer, "结束时间（Unix 秒，0=不限）", def: 0)));

        registry.Register(Tool(ToolCategory.Context, "get_group_members", "获取群聊成员列表。",
            P("chatroom_id", ToolParamType.String, "群聊 id", required: true)));

        registry.Register(Tool(ToolCategory.Analysis, "get_session_stats", "获取会话统计（消息量/活跃日/发言 TopN）。session_id 为空时只统计最近活跃的若干会话，避免全量扫描超时。",
            P("session_id", ToolParamType.String, "会话 id（空=扫最近活跃的 session_limit 个）", def: ""),
            P("limit", ToolParamType.Integer, "单会话样本条数", def: 200, min: 1, max: 1000),
            P("session_limit", ToolParamType.Integer, "无 session_id 时统计的会话数", def: 10, min: 1, max: 50),
            P("begin", ToolParamType.Integer, "开始时间（Unix 秒，0=不限）", def: 0),
            P("end", ToolParamType.Integer, "结束时间（Unix 秒，0=不限）", def: 0)));

        registry.Register(Tool(ToolCategory.Memory, "retrieve_memory",
            "基于语义检索从记忆知识库召回相关聊天片段（RAG）。**来自本地索引，可能滞后于今天的新消息**；要最新记录请用 read_messages。",
            P("query", ToolParamType.String, "检索语义", required: true),
            P("top_k", ToolParamType.Integer, "返回片段数", def: 5, min: 1, max: 20)));
    }

    private ToolDefinition Tool(string category, string name, string description, params ToolParameterSpec[] parameters)
    {
        // retrieve_memory 走 RAG 语义检索（rag_search），其余走同名 bridge 方法。
        var method = name == "retrieve_memory" ? "rag_search" : name;
        var specs = parameters.ToList();
        return new ToolDefinition
        {
            Name = name,
            Description = description,
            Category = category,
            Parameters = specs,
            ExecuteAsync = (argsJson, ct) => ExecuteAsync(method, specs, argsJson, ct),
        };
    }

    private async Task<ToolCallResult> ExecuteAsync(
        string method,
        IReadOnlyList<ToolParameterSpec> specs,
        string argsJson,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var args = new Dictionary<string, object?>();
        try
        {
            if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}")
            {
                using var doc = JsonDocument.Parse(argsJson);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    args[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString(),
                        JsonValueKind.Number => p.Value.GetInt64(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => p.Value.GetRawText(),
                    };
                }
            }
            // 按 spec 填默认值（仅当模型未提供且声明了默认值）
            foreach (var spec in specs)
            {
                if (spec.Default is not null && !args.ContainsKey(spec.Name))
                    args[spec.Name] = spec.Default;
            }

            // 节流壳：EcoMode 下把单次读取钳制到小样本（验收/自检省 token，避免全量拉取）。
            if (_agentOptions.EcoMode)
                ApplyEcoClamps(method, args);

            var resp = await _bridge.RequestAsync(method, args, ct: ct);
            var elapsedMs = (DateTime.UtcNow - started).TotalMilliseconds;
            if (!resp.Success)
            {
                return new ToolCallResult
                {
                    Success = false,
                    Error = resp.Error ?? "bridge error",
                    ElapsedMs = elapsedMs,
                };
            }

            var output = resp.Data is JsonElement je
                ? je.GetRawText()
                : JsonSerializer.Serialize(resp.Data, JsonOpts);

            var limit = _agentOptions.MaxToolResultChars;
            var omitted = output.Length > limit;
            return new ToolCallResult
            {
                Success = true,
                Output = omitted ? output[..limit] + "\n...[截断]" : output,
                IsOmitted = omitted,
                ElapsedMs = elapsedMs,
            };
        }
        catch (Exception ex)
        {
            return new ToolCallResult
            {
                Success = false,
                Error = ex.Message,
                ElapsedMs = (DateTime.UtcNow - started).TotalMilliseconds,
            };
        }
    }

    /// <summary>Eco 钳制：按工具把会拉大量数据的参数压到配置上限。0 值参数跳过（0=不限）。</summary>
    private void ApplyEcoClamps(string method, Dictionary<string, object?> args)
    {
        void Clamp(string key, int cap)
        {
            if (cap <= 0) return;
            if (!args.TryGetValue(key, out var v)) return;
            // 模型参数解析后为 long；默认值为装箱 int，两种都要覆盖。
            long current = v switch { long l => l, int i => i, _ => 0 };
            if (current > 0 && current > cap)
                args[key] = cap;
        }

        switch (method)
        {
            case "list_sessions":
            case "find_sessions":
                Clamp("limit", _agentOptions.EcoMaxSessions);
                break;
            case "read_messages":
            case "search_messages":
                Clamp("limit", _agentOptions.EcoMaxReadLimit);
                break;
            case "get_session_stats":
                Clamp("limit", _agentOptions.EcoMaxReadLimit);
                Clamp("session_limit", _agentOptions.EcoMaxSessionLimit);
                break;
            case "rag_search":
                Clamp("top_k", _agentOptions.EcoMaxTopK);
                break;
        }
    }
}
