using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Infrastructure.Agent;

/// <summary>
/// DeepSeek / OpenAI 兼容的 IChatClient。
/// 用原生 HttpClient + System.Text.Json，无第三方依赖。
/// 支持 Function Calling、指数退避重试（429/5xx）、JSON 解析容错。
/// </summary>
public sealed class DeepSeekChatClient : IChatClient, IStreamingChatClient, IToolChoiceChatClient
{
    public DeepSeekChatClient(
        LlmOptions options,
        IAppLogger? logger = null,
        HttpClient? http = null,
        int retryCount = 2)
    {
        _options = options;
        _logger = logger;
        _retryCount = retryCount;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    private readonly LlmOptions _options;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _http;
    private readonly int _retryCount;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ModelName => _options.Model;

    public async Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSchema>? tools = null,
        CancellationToken ct = default)
    {
        var attempts = 0;
        var maxAttempts = 1 + Math.Max(0, _retryCount);
        while (true)
        {
            attempts++;
            try
            {
                return await SendAsync(messages, tools, ct);
            }
            catch (HttpRequestException ex)
            {
                if (attempts >= maxAttempts) throw;
                var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempts - 1));
                _logger?.Warn($"LLM 请求失败(网络, {ex.Message})，{delay.TotalMilliseconds:0}ms 后重试 ({attempts}/{maxAttempts})");
                await Task.Delay(delay, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempts >= maxAttempts) throw;
                var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempts - 1));
                _logger?.Warn($"LLM 请求超时，{delay.TotalMilliseconds:0}ms 后重试 ({attempts}/{maxAttempts})");
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>
    /// 强制工具调用（第三阶段补充）：把 tool_choice 钉到具体函数上，让"该调的工具"变成协议层要求。
    /// 复用同一个 <see cref="SendAsync"/>（多一个 forcedToolName 参数），不另起一条请求路径。
    /// </summary>
    public Task<ChatResult> ChatWithForcedToolAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSchema> tools,
        string forcedToolName,
        Action<string>? reasoningDelta = null,
        CancellationToken ct = default)
        => SendAsync(messages, tools, ct, forcedToolName);

    private async Task<ChatResult> SendAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSchema>? tools,
        CancellationToken ct,
        string? forcedToolName = null)
    {
        var req = BuildRequest(BuildBody(messages, tools, stream: false, forcedToolName));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is System.Net.HttpStatusCode.TooManyRequests or >= System.Net.HttpStatusCode.InternalServerError)
            {
                throw new HttpRequestException($"LLM {resp.StatusCode}: {Truncate(text)}");
            }
            throw new HttpRequestException($"LLM {resp.StatusCode}: {Truncate(text)}");
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        var content = message.TryGetProperty("content", out var c) && c.ValueKind != JsonValueKind.Null
            ? c.GetString()
            : null;

        // 推理模型（deepseek-reasoner）的思考过程：只接住用于展示，**不回填 messages**
        // （DeepSeek 明确要求 reasoning_content 不得出现在后续请求里，否则 400）。
        var reasoning = message.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind != JsonValueKind.Null
            ? rc.GetString()
            : null;

        var toolCalls = ParseToolCalls(message);
        var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        var (promptTokens, completionTokens) = ParseUsage(root);

        return new ChatResult
        {
            Content = content,
            ReasoningContent = reasoning,
            ToolCalls = toolCalls,
            FinishReason = finish,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
        };
    }

    /// <summary>
    /// 流式对话（SSE）：正文/思考边生成边回调，返回的仍是完整结果（含 tool_calls 拼装）。
    /// 关键点：工具调用的参数是**分片**下发的（delta.tool_calls[].function.arguments 一片一片来），
    /// 必须按 index 累积再拼接，否则参数 JSON 是断的。
    /// 万一端点不支持 stream，会在"还没吐出任何增量"时退回一次性调用，功能不倒退。
    /// </summary>
    public async Task<ChatResult> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSchema>? tools,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta,
        CancellationToken ct = default)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, ToolCallAccumulator>();
        string? finish = null;
        int promptTokens = 0, completionTokens = 0;
        bool anyDelta = false;

        try
        {
            var req = BuildRequest(BuildBody(messages, tools, stream: true));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"LLM {resp.StatusCode}: {Truncate(err)}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                ct.ThrowIfCancellationRequested();
                // SSE 帧：`data: {json}`；空行/`event:`/注释一律跳过
                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line[5..].Trim();
                if (payload.Length == 0) continue;
                if (payload == "[DONE]") break;

                anyDelta |= ReadChunk(payload, content, reasoning, calls, ref finish, ref promptTokens, ref completionTokens,
                    onContentDelta, onReasoningDelta);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!anyDelta)
        {
            // 还没吐出任何增量 → 退回一次性调用（例如端点不支持 stream）
            _logger?.Warn($"流式请求失败（{ex.Message}），退回一次性调用。");
            return await SendAsync(messages, tools, ct);
        }

        return new ChatResult
        {
            Content = content.Length > 0 ? content.ToString() : null,
            ReasoningContent = reasoning.Length > 0 ? reasoning.ToString() : null,
            ToolCalls = calls.Count > 0
                ? calls.Values.Select(a => new ToolCallRequest
                {
                    Id = a.Id.Length > 0 ? a.Id : Guid.NewGuid().ToString("N"),
                    Name = a.Name,
                    ArgumentsJson = a.Arguments.Length > 0 ? a.Arguments.ToString() : "{}",
                }).ToList()
                : null,
            FinishReason = finish,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
        };
    }

    private sealed class ToolCallAccumulator
    {
        public string Id = "";
        public string Name = "";
        public readonly StringBuilder Arguments = new();
    }

    /// <summary>解析一帧 SSE，返回"这一帧是否带来了可见增量"。</summary>
    private static bool ReadChunk(
        string payload,
        StringBuilder content,
        StringBuilder reasoning,
        SortedDictionary<int, ToolCallAccumulator> calls,
        ref string? finish,
        ref int promptTokens,
        ref int completionTokens,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var (pt, ctok) = ParseUsage(root);
        if (pt > 0) promptTokens = pt;
        if (ctok > 0) completionTokens = ctok;

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return false;

        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            finish = fr.GetString();

        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            return false;

        bool got = false;

        if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
        {
            var s = c.GetString();
            if (!string.IsNullOrEmpty(s))
            {
                content.Append(s);
                onContentDelta?.Invoke(s);
                got = true;
            }
        }

        if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
        {
            var s = rc.GetString();
            if (!string.IsNullOrEmpty(s))
            {
                reasoning.Append(s);
                onReasoningDelta?.Invoke(s);
                got = true;
            }
        }

        // tool_calls 增量：按 index 累积（name/id 通常只有首帧，arguments 会被切成很多片）
        if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcs.EnumerateArray())
            {
                if (tc.ValueKind != JsonValueKind.Object) continue;
                int idx = tc.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i) ? i : 0;
                if (!calls.TryGetValue(idx, out var acc))
                {
                    acc = new ToolCallAccumulator();
                    calls[idx] = acc;
                }
                if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    acc.Id = id.GetString() ?? acc.Id;
                if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                {
                    if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                        acc.Name = nm.GetString() ?? acc.Name;
                    if (fn.TryGetProperty("arguments", out var ag) && ag.ValueKind == JsonValueKind.String)
                        acc.Arguments.Append(ag.GetString());
                }
                got = true;
            }
        }

        return got;
    }

    private JsonObject BuildBody(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSchema>? tools,
        bool stream,
        string? forcedToolName = null)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["temperature"] = _options.Temperature,
        };
        if (stream) body["stream"] = true;

        var msgArr = new JsonArray();
        foreach (var m in messages)
        {
            var obj = new JsonObject { ["role"] = m.Role };
            if (m.Content is not null) obj["content"] = m.Content;
            if (m.Role == "tool" && m.ToolCallId is not null)
                obj["tool_call_id"] = m.ToolCallId;
            if (m.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var tc in m.ToolCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["id"] = tc.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tc.Name,
                            ["arguments"] = tc.ArgumentsJson,
                        },
                    });
                }
                obj["tool_calls"] = calls;
            }
            msgArr.Add(obj);
        }
        body["messages"] = msgArr;

        if (tools is { Count: > 0 })
        {
            var toolsArr = new JsonArray();
            foreach (var t in tools)
            {
                toolsArr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        // DeepClone：同一 ToolSchema 可能被重试/多轮复用，JsonNode 不能重复挂父节点。
                        ["parameters"] = (t.Parameters ?? new JsonObject()).DeepClone(),
                    },
                });
            }
            body["tools"] = toolsArr;

            // tool_choice：把"这一步必须调这个函数"变成协议层要求。
            // 只在明确要求时下发（DeepSeek/OpenAI 都支持 {"type":"function","function":{"name":…}}）。
            if (!string.IsNullOrWhiteSpace(forcedToolName))
                body["tool_choice"] = new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = forcedToolName },
                };
        }
        return body;
    }

    private HttpRequestMessage BuildRequest(JsonObject body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(JsonOpts), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        return req;
    }

    /// <summary>解析非流式响应里的 tool_calls 数组。</summary>
    private static IReadOnlyList<ToolCallRequest>? ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var tcs)
            || tcs.ValueKind != JsonValueKind.Array || tcs.GetArrayLength() == 0)
            return null;

        var list = new List<ToolCallRequest>();
        foreach (var tc in tcs.EnumerateArray())
        {
            var fn = tc.GetProperty("function");
            list.Add(new ToolCallRequest
            {
                Id = tc.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                Name = fn.GetProperty("name").GetString() ?? "",
                ArgumentsJson = fn.TryGetProperty("arguments", out var args) ? args.GetString() ?? "{}" : "{}",
            });
        }
        return list;
    }

    private static (int Prompt, int Completion) ParseUsage(JsonElement root)
    {
        // usage 缺失时不能对 default(JsonElement) 调 TryGetProperty（会抛 InvalidOperationException）。
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return (0, 0);
        int pt = u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
        int ct = u.TryGetProperty("completion_tokens", out var q) && q.TryGetInt32(out var qv) ? qv : 0;
        return (pt, ct);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
