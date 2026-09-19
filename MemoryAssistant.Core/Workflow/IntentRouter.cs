using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Core.Workflow;

/// <summary>
/// 意图路由：轻量 LLM few-shot 输出 {intent, entity, time_hint}，
/// 解析失败或 LLM 不可用时用规则兜底（关键词）。
/// chitchat 不进入 RAG、不调用工具（降低算力消耗，对应计划 §12）。
/// </summary>
public sealed class IntentRouter
{
    public IntentRouter(IChatClient chatClient, IAppLogger? logger = null)
    {
        _chat = chatClient;
        _logger = logger;
    }

    private readonly IChatClient _chat;
    private readonly IAppLogger? _logger;

    private static readonly string SystemPrompt = """
你是一个意图分类器，判断用户对"微信聊天记录回忆助手"的提问意图。

意图定义：
- recall：回忆/查找过去聊过的内容（"我和X聊过什么""去年这个时候在干嘛""找之前提到XX的消息"）
- stats：统计/排行/数量（"谁和我聊天最多""这个群多少人""最近聊了多少条""什么时候最频繁"）
- commitment：承诺/待办（"我答应过谁什么""我还有哪些事情没做""答应过别人的事""记得提醒我"）
- chitchat：与聊天记录无关的普通对话（"你好""今天天气怎么样""谢谢"）

只输出 JSON，不要多余文字：
{"intent": "recall|stats|commitment|chitchat", "entity": "涉及的人名或群名，无则空", "time_hint": "时间线索如'去年''最近一个月'，无则空"}
""";

    public async Task<IntentResult> RouteAsync(string query, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = SystemPrompt },
                new() { Role = "user", Content = query },
            };
            var result = await _chat.ChatAsync(messages, tools: null, ct);
            var parsed = Parse(result.Content ?? "");
            if (parsed is not null)
            {
                _logger?.Debug($"意图路由: {parsed.Intent} entity='{parsed.Entity}' time='{parsed.TimeHint}' ({sw.ElapsedMilliseconds}ms)");
                return parsed;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"意图路由 LLM 失败，改用规则: {ex.Message}");
        }
        var fallback = RouteByRule(query);
        _logger?.Debug($"意图路由(规则): {fallback.Intent} ({sw.ElapsedMilliseconds}ms)");
        return fallback;
    }

    private static IntentResult? Parse(string content)
    {
        // 提取 JSON 对象（容忍 markdown 代码块包裹）
        var json = content;
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        json = json[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var intentStr = root.TryGetProperty("intent", out var i) ? i.GetString() ?? "" : "";
            var intent = intentStr.ToLowerInvariant() switch
            {
                "recall" => IntentKind.Recall,
                "stats" => IntentKind.Stats,
                "commitment" => IntentKind.Commitment,
                "chitchat" => IntentKind.Chitchat,
                _ => IntentKind.Unknown,
            };
            if (intent == IntentKind.Unknown) return null;
            return new IntentResult
            {
                Intent = intent,
                Entity = root.TryGetProperty("entity", out var e) ? e.GetString() ?? "" : "",
                TimeHint = root.TryGetProperty("time_hint", out var t) ? t.GetString() ?? "" : "",
                Raw = content,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly (IntentKind Intent, string[] Words)[] RuleTable =
    [
        // 顺序敏感：commitment 必须在 recall 之前（"答应/承诺/没做"等词）
        (IntentKind.Commitment, ["答应", "承诺", "还没做", "没做", "要办", "待办", "提醒我", "答应过"]),
        (IntentKind.Recall, ["聊过", "上次", "什么时候", "找", "以前", "去年", "之前", "说过", "回忆", "最近讨论", "在聊"]),
        (IntentKind.Stats, ["最活跃", "最多", "多少条", "排名", "统计", "数量", "频繁", "活跃", "排行", "多少消息", "聊了几天", "哪些人"]),
        (IntentKind.Chitchat, ["你好", "天气", "谢谢", "再见", "你好呀", "在吗", "你是谁", "你能做什么"]),
    ];

    private static IntentResult RouteByRule(string query)
    {
        foreach (var (intent, words) in RuleTable)
        {
            if (words.Any(w => query.Contains(w, StringComparison.Ordinal)))
            {
                return new IntentResult { Intent = intent, FromRule = true, Raw = query };
            }
        }
        // 默认：含疑问词且无闲聊特征 → recall（回忆类为主）
        return new IntentResult { Intent = IntentKind.Recall, FromRule = true, Raw = query };
    }
}
