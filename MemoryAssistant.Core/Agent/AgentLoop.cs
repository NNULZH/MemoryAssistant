using System.Diagnostics;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Configuration;

namespace MemoryAssistant.Core.Agent;

/// <summary>
/// ReAct 风格 Agent 主循环：
///   system + 历史 → LLM(tools) → 有 tool_calls 则执行并回填 → 循环，直至无调用或达 max_rounds。
/// 确定性由上层 Workflow 保证，这里只做通用循环。
/// </summary>
public sealed class AgentLoop
{
    public AgentLoop(IChatClient chatClient, ToolRegistry tools, AgentOptions options, Core.Logging.IAppLogger? logger = null)
    {
        _chat = chatClient;
        _tools = tools;
        _options = options;
        _logger = logger;
    }

    private readonly IChatClient _chat;
    private readonly ToolRegistry _tools;
    private readonly AgentOptions _options;
    private readonly Core.Logging.IAppLogger? _logger;

    public AgentOptions Options => _options;

    /// <summary>单次 Agent 运行最多沉淀多少条工具证据（控 UI 体积与作答上下文）。</summary>
    private const int MaxToolEvidence = 40;

    /// <param name="onThinking">思考增量回调（非空且客户端支持流式时，reasoning 边生成边推）。</param>
    /// <param name="onTool">工具调用回调：每次调用推 start/finish 两条，供 UI 单独凸出展示"调了什么、拿到什么"。</param>
    /// <param name="requireToolName">
    /// 本轮**必须真的调用**的工具名（第三阶段补充：用户明确要求"发消息/回复"这类外部动作时用）。
    /// 首轮走 tool_choice 强制（客户端支持的话），之后若模型始终不动手，则纠正一次；
    /// 仍然没调用 → 本轮**不算正常完成**（CompletedNormally=false），由上层如实汇报"没有执行"。
    /// </param>
    public async Task<AgentResult> RunAsync(
        string systemPrompt,
        string userQuery,
        AgentContext? context = null,
        CancellationToken ct = default,
        Action<string>? onThinking = null,
        Action<AgentProgress>? onTool = null,
        string? requireToolName = null)
    {
        var sw = Stopwatch.StartNew();
        var messages = new List<ChatMessage>(context?.Messages ?? []);
        // 会话上下文（workflow 预检索结果）由上层以 system/user 消息前置。
        if (context is null) messages.Clear();
        messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
        messages.Add(new ChatMessage { Role = "user", Content = userQuery });
        // 写操作要求：先把"必须真的调用"写进对话（不支持 tool_choice 的客户端全靠这句兜底）
        if (!string.IsNullOrWhiteSpace(requireToolName))
            messages.Add(new ChatMessage { Role = "user", Content = WriteIntentGuard.CorrectionInstruction(requireToolName, userQuery) });

        var allToolCalls = new List<ToolCallRequest>();
        var rounds = new List<AgentRoundTrace>();
        var toolEvidence = new List<Evidence>();
        var evidenceKeys = new HashSet<string>(StringComparer.Ordinal);
        int totalPrompt = 0, totalCompletion = 0;
        var tools = _tools.BuildSchemas();
        int round = 0;
        string? earlyStop = null;
        bool corrected = false;   // 每轮最多纠正一次"把工具调用写成正文"
        bool correctedMissingWrite = false;   // 每轮最多纠正一次"该调写操作工具却没调"
        var writeAttemptedTools = new List<string>();   // 真的发起过的写操作工具调用（含失败）

        while (round < _options.MaxRounds)
        {
            ct.ThrowIfCancellationRequested();
            round++;

            var roundSw = Stopwatch.StartNew();
            ChatResult result;
            // 首轮：要求写操作时优先用 tool_choice 把"必须调的工具"钉死（客户端不支持则靠上面那句要求兜底）
            if (requireToolName is not null && round == 1 && tools.Count > 0
                && _chat is IToolChoiceChatClient forcedChat)
            {
                _logger?.Info($"[AgentLoop] 本轮要求写操作，tool_choice 钉到 {requireToolName}");
                result = await forcedChat.ChatWithForcedToolAsync(messages, tools, requireToolName, onThinking, ct);
            }
            // 有思考回调且客户端支持流式 → 走流式，让"模型在想什么"边生成边可见
            else if (onThinking is not null && _chat is IStreamingChatClient streaming)
            {
                result = await streaming.ChatStreamAsync(messages, tools, null, onThinking, ct);
            }
            else
            {
                result = await _chat.ChatAsync(messages, tools, ct);
            }
            totalPrompt += result.PromptTokens;
            totalCompletion += result.CompletionTokens;
            // 只要这一轮"有内容或有工具调用"，就要把 assistant 消息写入历史（带上 tool_calls）。
            // 模型发起工具调用时 content 通常为空，若因此不入历史，回填的 tool 消息就会缺少
            // 前驱 tool_calls，OpenAI/DeepSeek 会直接报 400（"must be a response to a preceding tool_calls"）。
            if (result.HasToolCalls || result.Content is { Length: > 0 })
                messages.Add(new ChatMessage { Role = "assistant", Content = result.Content, ToolCalls = result.ToolCalls });

            if (!result.HasToolCalls)
            {
                var answer = StripDsmlMarkers(result.Content ?? "");
                roundSw.Stop();

                // 模型偶尔不走协议：把工具调用写成了正文（DSML/XML 文本标记），于是"既没调工具、也没回答"。
                // 直接把清洗后的空串交出去，用户什么也看不到，所以这里回一句纠正让它重说一次。
                if (answer.Length == 0 && !string.IsNullOrWhiteSpace(result.Content)
                    && !corrected && round < _options.MaxRounds)
                {
                    corrected = true;
                    _logger?.Warn($"Agent 第 {round} 轮把工具调用写成了正文（非协议 tool_calls），已要求重新作答。");
                    rounds.Add(new AgentRoundTrace
                    {
                        Round = round,
                        LatencyMs = roundSw.Elapsed.TotalMilliseconds,
                        PromptTokens = result.PromptTokens,
                        CompletionTokens = result.CompletionTokens,
                        FinishReason = result.FinishReason,
                        Reasoning = result.ReasoningContent,
                        AssistantContent = result.Content,
                    });
                    messages.Add(new ChatMessage { Role = "user", Content = LeakCorrectionPrompt });
                    continue;
                }

                rounds.Add(new AgentRoundTrace
                {
                    Round = round,
                    LatencyMs = roundSw.Elapsed.TotalMilliseconds,
                    PromptTokens = result.PromptTokens,
                    CompletionTokens = result.CompletionTokens,
                    FinishReason = result.FinishReason,
                    Reasoning = result.ReasoningContent,
                    AssistantContent = result.Content,
                });

                // 该调写操作工具却没调 → **不允许就这么结束本轮**（第三阶段补充 §3）。
                // 只纠正一次：给它一次按协议动手的机会；再不动手就如实标成"没执行"，由上层汇报。
                if (requireToolName is not null && writeAttemptedTools.Count == 0
                    && !correctedMissingWrite && round < _options.MaxRounds)
                {
                    correctedMissingWrite = true;
                    _logger?.Warn($"[AgentLoop] 要求调用 {requireToolName} 但模型只回了文字，已纠正一次（不允许结束本轮）。");
                    messages.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = WriteIntentGuard.CorrectionInstruction(requireToolName, userQuery),
                    });
                    continue;
                }

                // 到这一步还没动手 → 本轮不算正常完成；reason 里说清"是写操作没执行"，别让上层当成普通回答。
                bool unmetWrite = requireToolName is not null && writeAttemptedTools.Count == 0;
                if (unmetWrite)
                    earlyStop = $"写操作未执行：{requireToolName} 没有被调用（模型只生成了文字）";

                _logger?.Debug($"Agent 第 {round} 轮无工具调用，正常结束。");
                return new AgentResult
                {
                    Answer = answer,
                    AllToolCalls = allToolCalls,
                    RoundCount = round,
                    CompletedNormally = !unmetWrite,
                    EarlyStopReason = unmetWrite ? earlyStop : null,
                    TotalElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Rounds = rounds,
                    TotalPromptTokens = totalPrompt,
                    TotalCompletionTokens = totalCompletion,
                    Evidence = toolEvidence,
                };
            }

            // 执行工具
            allToolCalls.AddRange(result.ToolCalls!);
            var toolTraces = new List<ToolCallTrace>();
            foreach (var call in result.ToolCalls!)
            {
                ct.ThrowIfCancellationRequested();
                // 记下"真的发起过写操作工具调用"（含失败）：这是"用户要求发消息"是否被兑现的唯一判据
                if (WriteIntentGuard.WriteToolNames.Contains(call.Name, StringComparer.OrdinalIgnoreCase))
                    writeAttemptedTools.Add(call.Name);
                // 工具调用单独推给人看：光有思考文字时，"模型说要去查"和"模型真的查了"长得一模一样
                onTool?.Invoke(AgentProgress.ToolStart(call.Name, AgentProgress.CompactArguments(call.ArgumentsJson)));
                var toolResult = await ExecuteToolAsync(call, ct);
                onTool?.Invoke(AgentProgress.ToolEnd(call.Name, toolResult.Success,
                    toolResult.Success ? toolResult.Output : (toolResult.Error ?? "工具执行失败")));
                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = call.Id,
                    // 失败也要如实回填给模型（否则它会以为工具"什么都没返回"而继续瞎猜）
                    Content = toolResult.Success
                        ? toolResult.Output
                        : $"ERROR: {toolResult.Error ?? "工具执行失败"}",
                });
                toolTraces.Add(new ToolCallTrace
                {
                    ToolCallId = call.Id,
                    Name = call.Name,
                    ArgumentsJson = Truncate(call.ArgumentsJson, 300),
                    Success = toolResult.Success,
                    Error = toolResult.Error,
                    Output = Truncate(toolResult.Output, 200),
                    IsOmitted = toolResult.IsOmitted,
                    LatencyMs = toolResult.ElapsedMs,
                    Round = round,
                });

                // 工具找到的原文/片段要落成证据：否则答案没有 [N] 引用、UI 也不知道"其实查过记录"。
                // 用**未截断**的原始输出解析（工具 trace 里的 Output 已被截到 200 字，解析不了）。
                if (toolResult.Success)
                {
                    foreach (var ev in ToolEvidenceExtractor.Extract(call.Name, call.ArgumentsJson, toolResult.Output))
                    {
                        if (toolEvidence.Count >= MaxToolEvidence) break;
                        if (evidenceKeys.Add($"{ev.SessionId}|{ev.CreateTime}|{ev.Content}"))
                            toolEvidence.Add(ev);
                    }
                }
            }
            roundSw.Stop();
            rounds.Add(new AgentRoundTrace
            {
                Round = round,
                LatencyMs = roundSw.Elapsed.TotalMilliseconds,
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                FinishReason = result.FinishReason,
                Reasoning = result.ReasoningContent,
                // 工具轮里模型往往先说一句"我来搜一下X" —— 这就是它的意图自述，必须留存
                AssistantContent = result.Content,
                ToolCalls = toolTraces,
            });
        }

        // 达 max_rounds：强制收尾，给模型一次无工具生成机会。
        earlyStop = requireToolName is not null && writeAttemptedTools.Count == 0
            ? $"max_rounds({_options.MaxRounds})，且写操作未执行：{requireToolName} 没有被调用"
            : $"max_rounds({_options.MaxRounds})";
        _logger?.Warn($"Agent 达到 {earlyStop}，强制生成最终答案。");
        var finalSw = Stopwatch.StartNew();
        // 收尾轮也要推思考（模型往往在这里把判断讲明白），但不要再给 tools：必须逼它出正文
        var final = onThinking is not null && _chat is IStreamingChatClient finalStreaming
            ? await finalStreaming.ChatStreamAsync(messages, null, null, onThinking, ct)
            : await _chat.ChatAsync(messages, tools: null, ct);
        messages.Add(new ChatMessage { Role = "assistant", Content = final.Content });
        var finalAnswer = StripDsmlMarkers(final.Content ?? "");
        int finalPrompt = final.PromptTokens, finalCompletion = final.CompletionTokens;
        var finalReasoning = final.ReasoningContent;
        var finalAssistant = final.Content;
        var finalFinish = final.FinishReason;

        // 收尾轮也可能只吐工具标记（模型还想着调工具）——那等于没回答，再明确要一次纯文字回答。
        if (finalAnswer.Length == 0 && !string.IsNullOrWhiteSpace(final.Content))
        {
            _logger?.Warn("收尾轮只输出了工具调用标记（非协议 tool_calls），再要一次纯文字回答。");
            messages.Add(new ChatMessage { Role = "user", Content = LeakCorrectionPrompt });
            var retry = await _chat.ChatAsync(messages, tools: null, ct);
            messages.Add(new ChatMessage { Role = "assistant", Content = retry.Content });
            finalPrompt += retry.PromptTokens;
            finalCompletion += retry.CompletionTokens;
            finalAnswer = StripDsmlMarkers(retry.Content ?? "");
            finalReasoning = retry.ReasoningContent;
            finalAssistant = retry.Content;
            finalFinish = retry.FinishReason;
        }
        finalSw.Stop();
        totalPrompt += finalPrompt;
        totalCompletion += finalCompletion;
        rounds.Add(new AgentRoundTrace
        {
            Round = round + 1,
            LatencyMs = finalSw.Elapsed.TotalMilliseconds,
            PromptTokens = finalPrompt,
            CompletionTokens = finalCompletion,
            FinishReason = finalFinish,
            Reasoning = finalReasoning,
            AssistantContent = finalAssistant,
        });

        return new AgentResult
        {
            Answer = finalAnswer,
            AllToolCalls = allToolCalls,
            RoundCount = round,
            CompletedNormally = false,
            EarlyStopReason = earlyStop,
            TotalElapsedMs = sw.Elapsed.TotalMilliseconds,
            Rounds = rounds,
            TotalPromptTokens = totalPrompt,
            TotalCompletionTokens = totalCompletion,
            Evidence = toolEvidence,
        };
    }

    /// <summary>模型把工具调用写成正文时的纠正提示（按协议重发 tool_calls，或直接用文字回答）。</summary>
    private const string LeakCorrectionPrompt =
        "你上一条消息只输出了工具调用标记（XML/DSML 文本），用户看不到任何内容。"
        + "请不要把工具调用写成普通文本；若还需要数据，请按协议发起 tool_calls，否则直接用中文文字回答用户。";

    /// <summary>泄漏标记家族：(开始前缀, 结束标签前缀, 终止符)。DeepSeek 两种形态都出现过。</summary>
    private static readonly (string Open, string ClosePrefix, char End)[] LeakFamilies =
    [
        ("＜｜DSML｜｜", "＜｜DSML｜｜/", '＞'),   // 全角 DSML 形态
        ("<", "</", '>'),                        // 半角 XML 形态（实测 flash/reasoner 都会吐）
    ];

    /// <summary>这些标签是"块"：要连同内部内容一起删掉（参数都在块里）。</summary>
    private static readonly string[] BlockTags = ["tool_calls", "tool_call", "function_calls", "invoke"];

    /// <summary>可能出现在泄漏里的标签名（用于判断半角 "&lt;" 到底是标记还是正文里的普通字符）。</summary>
    private static readonly string[] LeakTags = [.. BlockTags, "parameter"];

    /// <summary>
    /// 清洗模型输出中泄漏的工具调用标记。DeepSeek 有两种形态，必须都认：
    ///  - 全角 DSML：＜｜DSML｜｜tool_calls＞…＜｜DSML｜｜/tool_calls＞
    ///  - 半角 XML：&lt;tool_calls&gt;&lt;invoke&gt;&lt;parameter …&gt;&lt;/invoke&gt;…
    /// 只清理标记块，保留正文；块未闭合（被 max_tokens 截断）时丢弃标记之后的剩余内容。
    /// </summary>
    public static string StripDsmlMarkers(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        int pos = 0;
        while (pos < text.Length)
        {
            if (!FindLeakMarker(text, pos, out var start, out var family))
            {
                sb.Append(text, pos, text.Length - pos);
                break;
            }
            sb.Append(text, pos, start - pos);

            int tagEnd = text.IndexOf(family.End, start + family.Open.Length);
            if (tagEnd < 0) break;   // 标签未闭合 → 丢弃剩余

            var tag = text[(start + family.Open.Length)..tagEnd].Trim();
            var name = TagName(tag);
            bool isClose = tag.StartsWith('/');

            // 开始块标签（tool_calls / invoke）→ 连同块内容一起跳到对应结束标签之后
            if (!isClose && BlockTags.Contains(name, StringComparer.Ordinal))
            {
                var endMarker = family.ClosePrefix + name;
                int blockStart = text.IndexOf(endMarker, tagEnd + 1, StringComparison.Ordinal);
                if (blockStart < 0) break;   // 块未闭合（被截断）→ 丢弃剩余
                int blockEnd = text.IndexOf(family.End, blockStart + endMarker.Length);
                if (blockEnd < 0) break;
                pos = blockEnd + 1;
                continue;
            }
            pos = tagEnd + 1;   // 结束标签/独立标签：只丢标签本身
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 从 pos 起找最早的泄漏标记。半角 "&lt;" 只有后面跟的是已知标签名时才算标记，
    /// 否则正文里一个普通的 "&lt;" 会把后面的内容全部误删。
    /// </summary>
    private static bool FindLeakMarker(
        string text, int pos, out int start, out (string Open, string ClosePrefix, char End) family)
    {
        start = -1;
        family = default;
        foreach (var f in LeakFamilies)
        {
            int i = text.IndexOf(f.Open, pos, StringComparison.Ordinal);
            while (i >= 0 && f.Open == "<" && !IsHalfWidthTagStart(text, i))
                i = text.IndexOf(f.Open, i + 1, StringComparison.Ordinal);
            if (i < 0) continue;
            if (start < 0 || i < start)
            {
                start = i;
                family = f;
            }
        }
        return start >= 0;
    }

    private static bool IsHalfWidthTagStart(string text, int i)
    {
        int j = i + 1;
        if (j < text.Length && text[j] == '/') j++;
        int k = j;
        while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_')) k++;
        return k > j && LeakTags.Contains(text[j..k], StringComparer.Ordinal);
    }

    private static string TagName(string tag)
    {
        var t = tag.StartsWith('/') ? tag[1..] : tag;
        int sp = t.IndexOfAny([' ', '\t', '\r', '\n', '＞', '>']);
        return (sp >= 0 ? t[..sp] : t).Trim();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

    private async Task<ToolCallResult> ExecuteToolAsync(ToolCallRequest call, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!_tools.TryGet(call.Name, out var tool))
        {
            return new ToolCallResult
            {
                ToolCallId = call.Id,
                Success = false,
                Error = $"未知工具: {call.Name}",
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
        }

        try
        {
            var r = await tool.ExecuteAsync(call.ArgumentsJson, ct);
            return r with { ToolCallId = call.Id, ElapsedMs = r.ElapsedMs == 0 ? sw.Elapsed.TotalMilliseconds : r.ElapsedMs };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"工具 {call.Name} 异常: {ex.Message}");
            return new ToolCallResult
            {
                ToolCallId = call.Id,
                Success = false,
                Error = ex.Message,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
        }
    }
}
