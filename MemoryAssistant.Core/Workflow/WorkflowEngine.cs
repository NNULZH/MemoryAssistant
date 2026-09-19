using System.Diagnostics;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Features.Commitment;
using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Core.Workflow;

/// <summary>
/// 工作流引擎（对应计划 §11-13）：
///   用户输入 → Intent Router → 检索策略 → 候选检索(粗排) → 工具精读 → Evidence Merge → Agent/LLM → 带引用回答
///
/// 核心设计：
///   - Workflow 负责确定性（意图划分、两级检索、证据整合）
///   - Agent 负责灵活探索（工具选择、追问、二次读取）
///   - chitchat 不进入 RAG、不调用工具
/// </summary>
public sealed class WorkflowEngine
{
    public WorkflowEngine(
        IChatClient chatClient,
        IRetriever coarseRetriever,
        Func<string, long, long, int, Task<IReadOnlyList<ToolMessageInput>>> preciseReader,
        ToolRegistry tools,
        AgentOptions options,
        IAppLogger? logger = null,
        Func<Task<IReadOnlyList<CommitmentCandidate>>>? commitmentScanner = null)
    {
        _chat = chatClient;
        _coarse = coarseRetriever;
        _preciseReader = preciseReader;
        _tools = tools;
        _options = options;
        _logger = logger;
        _commitmentScanner = commitmentScanner;
        _router = new IntentRouter(chatClient, logger);
    }

    private readonly IChatClient _chat;
    private readonly IRetriever _coarse;
    private readonly Func<string, long, long, int, Task<IReadOnlyList<ToolMessageInput>>> _preciseReader;
    private readonly ToolRegistry _tools;
    private readonly AgentOptions _options;
    private readonly IAppLogger? _logger;
    private readonly IntentRouter _router;
    private readonly Func<Task<IReadOnlyList<CommitmentCandidate>>>? _commitmentScanner;

    public async Task<WorkflowResult> RunAsync(string query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var timings = new Dictionary<string, double>();
        var events = new List<WorkflowStageEvent>();
        var routerSw = Stopwatch.StartNew();
        var intent = await _router.RouteAsync(query, ct);
        routerSw.Stop();
        timings["intent"] = routerSw.Elapsed.TotalMilliseconds;
        events.Add(new WorkflowStageEvent("intent", $"意图: {intent.Intent} entity='{intent.Entity}'"));
        _logger?.Info($"[Workflow] 意图: {intent.Intent} entity='{intent.Entity}'");

        switch (intent.Intent)
        {
            case IntentKind.Chitchat:
                return await HandleChitchatAsync(intent, query, sw, timings, events, ct);
            case IntentKind.Stats:
                return await HandleStatsAsync(intent, query, sw, timings, events, ct);
            case IntentKind.Commitment:
                return await HandleCommitmentAsync(intent, query, sw, timings, events, ct);
            case IntentKind.Recall:
            default:
                return await HandleRecallAsync(intent, query, sw, timings, events, ct);
        }
    }

    // ---- 分支：闲聊 ----

    private async Task<WorkflowResult> HandleChitchatAsync(
        IntentResult intent, string query, Stopwatch sw, Dictionary<string, double> timings,
        List<WorkflowStageEvent> events, CancellationToken ct)
    {
        events.Add(new WorkflowStageEvent("chitchat", "闲聊模式：不调用工具、不进入 RAG"));
        _logger?.Info("[Workflow] chitchat：不调用工具、不进入 RAG。");
        var loop = new AgentLoop(_chat, _tools, _options, _logger);
        var agentResult = await loop.RunAsync(ChitchatSystem, query, ct: ct);
        sw.Stop();
        timings["llm"] = agentResult.TotalElapsedMs;
        events.Add(new WorkflowStageEvent("llm", $"LLM {agentResult.RoundCount} 轮 {agentResult.TotalElapsedMs:0}ms"));
        return new WorkflowResult
        {
            Answer = agentResult.Answer,
            Intent = intent,
            AgentResult = agentResult,
            TotalElapsedMs = sw.Elapsed.TotalMilliseconds,
            StageTimings = timings,
            StageEvents = events,
        };
    }

    private const string ChitchatSystem = """
你是"回忆助手"的闲聊模式。这是一个微信聊天记录回忆工具，但当前对话与聊天记录无关。
请直接友好回答，不要编造任何聊天记录内容。
""";

    // ---- 分支：统计 ----

    private async Task<WorkflowResult> HandleStatsAsync(
        IntentResult intent, string query, Stopwatch sw, Dictionary<string, double> timings,
        List<WorkflowStageEvent> events, CancellationToken ct)
    {
        var system = """
你是"回忆助手"。用户问的是聊天数据统计类问题。
可用工具：get_session_stats（会话统计）、list_sessions（找会话）。
规则：
1. 基于工具返回的真实数据回答，给出具体数字。
2. 若数据不足，诚实说明。
3. 回答简洁。
""";
        var loop = new AgentLoop(_chat, _tools, _options, _logger);
        var agentResult = await loop.RunAsync(system, query, ct: ct);
        sw.Stop();
        timings["llm"] = agentResult.TotalElapsedMs;
        events.Add(new WorkflowStageEvent("llm", $"LLM {agentResult.RoundCount} 轮 {agentResult.TotalElapsedMs:0}ms"));
        return new WorkflowResult
        {
            Answer = agentResult.Answer,
            Intent = intent,
            AgentResult = agentResult,
            TotalElapsedMs = sw.Elapsed.TotalMilliseconds,
            StageTimings = timings,
            StageEvents = events,
        };
    }

    // ---- 分支：承诺（P7：规则初筛 → 证据 → LLM 应答） ----

    private async Task<WorkflowResult> HandleCommitmentAsync(
        IntentResult intent, string query, Stopwatch sw, Dictionary<string, double> timings,
        List<WorkflowStageEvent> events, CancellationToken ct)
    {
        var candidates = new List<CommitmentCandidate>();
        if (_commitmentScanner is not null)
        {
            var scanSw = Stopwatch.StartNew();
            try
            {
                candidates = (await _commitmentScanner()).ToList();
                events.Add(new WorkflowStageEvent("scan_commitments", $"扫描到 {candidates.Count} 条候选"));
            }
            catch (Exception ex)
            {
                events.Add(new WorkflowStageEvent("scan_commitments", $"承诺扫描失败: {ex.Message}"));
                _logger?.Warn($"[Workflow] 承诺扫描失败: {ex.Message}");
            }
            scanSw.Stop();
            timings["scan_commitments"] = scanSw.Elapsed.TotalMilliseconds;
        }
        _logger?.Info($"[Workflow] 承诺候选 {candidates.Count} 条");

        var evidence = EvidenceMerger.FromCommitmentCandidates(candidates);
        events.Add(new WorkflowStageEvent("evidence", $"证据 {evidence.Count} 条"));
        var system = BuildCommitmentSystemPrompt(evidence, candidates);

        var loop = new AgentLoop(_chat, _tools, _options, _logger);
        var agentResult = await loop.RunAsync(system, query, ct: ct);
        sw.Stop();
        timings["llm"] = agentResult.TotalElapsedMs;
        events.Add(new WorkflowStageEvent("llm", $"LLM {agentResult.RoundCount} 轮 {agentResult.TotalElapsedMs:0}ms"));
        return new WorkflowResult
        {
            Answer = agentResult.Answer,
            Intent = intent,
            Evidence = evidence,
            AgentResult = agentResult,
            TotalElapsedMs = sw.Elapsed.TotalMilliseconds,
            StageTimings = timings,
            StageEvents = events,
        };
    }

    private static string BuildCommitmentSystemPrompt(
        IReadOnlyList<Evidence> evidence, IReadOnlyList<CommitmentCandidate> candidates)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("""
你是"回忆助手"。用户问的是聊天中"承诺/待办"类问题（答应过别人的事、还没做的事、提醒事项）。
可用工具：list_sessions、read_messages、search_messages、get_group_members、get_session_stats、retrieve_memory。
""");
        if (evidence.Count > 0)
        {
            sb.Append("\n【规则识别的承诺候选（未确认，可能误报，需谨慎判断）】\n");
            foreach (var e in evidence)
            {
                var when = e.CreateTime > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(e.CreateTime).ToLocalTime().ToString("yyyy-MM-dd")
                    : "";
                sb.Append($"[{e.Index}] ({e.Source}) {when} {e.SessionDisplayName} {e.SenderName}: {e.Content}\n");
            }
        }
        sb.Append("""

规则：
1. 候选来自关键词规则初筛，可能有误报，回答时注意甄别语气与上下文。
2. 区分"我的承诺"（我答应别人）与"他人对我的承诺"（别人答应我）；从消息内容和发送者判断。
3. 引用候选时用 [N] 标注来源。候选已覆盖全部索引历史，通常足够直接回答，不要调用工具去验证候选归属的会话（它们都来自本地索引）。
4. 按时间列出，标注"待确认"；如果候选不足，诚实说明样本范围（规则扫描的索引范围）。
5. 回答简洁，直接给结论（承诺内容 + 相关会话 + 时间），不要描述你的查找过程。
""");
        return sb.ToString();
    }

    // ---- 分支：回忆（两级检索 + 证据 + 引用） ----

    private async Task<WorkflowResult> HandleRecallAsync(
        IntentResult intent, string query, Stopwatch sw, Dictionary<string, double> timings,
        List<WorkflowStageEvent> events, CancellationToken ct)
    {
        // 阶段1：粗排 —— RAG/FTS 定位"哪个会话、哪段时间"
        var coarseSw = Stopwatch.StartNew();
        var coarse = await _coarse.SearchAsync(query, topK: 5, ct);
        coarseSw.Stop();
        timings["coarse_retrieval"] = coarseSw.Elapsed.TotalMilliseconds;
        events.Add(new WorkflowStageEvent("coarse_retrieval", $"粗排 {coarse.Count} 个候选片段"));
        _logger?.Info($"[Workflow] 粗排 {coarse.Count} 个候选片段");

        // 阶段2：精读 —— 对 top 候选会话读当天原文
        var preciseSw = Stopwatch.StartNew();
        var precise = new List<ToolMessageInput>();
        var probed = new HashSet<string>();
        foreach (var c in coarse.Take(3))
        {
            if (!probed.Add(c.SessionId)) continue;
            var (begin, end) = DayRange(c.Date);
            if (begin == 0) continue;
            try
            {
                var msgs = await _preciseReader(c.SessionId, begin, end, 30);
                precise.AddRange(msgs);
                events.Add(new WorkflowStageEvent("precise_read", $"精读 {c.SessionName} ({c.Date}) 得到 {msgs.Count} 条"));
                _logger?.Info($"[Workflow] 精读 {c.SessionName} ({c.Date}) 得到 {msgs.Count} 条");
            }
            catch (Exception ex)
            {
                events.Add(new WorkflowStageEvent("precise_read", $"精读失败 {c.SessionId}: {ex.Message}"));
                _logger?.Warn($"[Workflow] 精读失败 {c.SessionId}: {ex.Message}");
            }
        }
        preciseSw.Stop();
        timings["precise_read"] = preciseSw.Elapsed.TotalMilliseconds;

        // 证据整合
        var ragEvidence = EvidenceMerger.FromChunks(coarse, "rag");
        var chatEvidence = EvidenceMerger.FromToolMessages(precise, "微信原文");
        var evidence = EvidenceMerger.Merge(ragEvidence, chatEvidence);
        events.Add(new WorkflowStageEvent("evidence", $"证据 {evidence.Count} 条"));
        _logger?.Info($"[Workflow] 证据 {evidence.Count} 条");

        if (evidence.Count == 0)
        {
            events.Add(new WorkflowStageEvent("fallback", "证据为空，降级为纯 Agent 检索"));
            var fallback = await RunRecallAgentAsync(intent, query, null, ct);
            sw.Stop();
            timings["llm"] = fallback.TotalElapsedMs;
            events.Add(new WorkflowStageEvent("llm", $"LLM {fallback.RoundCount} 轮 {fallback.TotalElapsedMs:0}ms"));
            return new WorkflowResult
            {
                Answer = fallback.Answer,
                Intent = intent,
                AgentResult = fallback,
                TotalElapsedMs = sw.Elapsed.TotalMilliseconds,
                StageTimings = timings,
                StageEvents = events,
            };
        }

        // 生成：证据注入 + Agent 带工具
        var genSw = Stopwatch.StartNew();
        var agentResult = await RunRecallAgentAsync(intent, query, evidence, ct);
        genSw.Stop();
        timings["llm"] = genSw.Elapsed.TotalMilliseconds;
        events.Add(new WorkflowStageEvent("llm", $"LLM {agentResult.RoundCount} 轮 {genSw.Elapsed.TotalMilliseconds:0}ms"));

        sw.Stop();
        return new WorkflowResult
        {
            Answer = agentResult.Answer,
            Intent = intent,
            Evidence = evidence,
            AgentResult = agentResult,
            TotalElapsedMs = sw.Elapsed.TotalMilliseconds,
            StageTimings = timings,
            StageEvents = events,
        };
    }

    private async Task<AgentResult> RunRecallAgentAsync(
        IntentResult intent, string query, IReadOnlyList<Evidence>? evidence, CancellationToken ct)
    {
        var system = BuildRecallSystemPrompt(evidence);
        var loop = new AgentLoop(_chat, _tools, _options, _logger);
        return await loop.RunAsync(system, query, ct: ct);
    }

    private static string BuildRecallSystemPrompt(IReadOnlyList<Evidence>? evidence)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("""
你是"回忆助手"，帮助用户回忆微信聊天记录中的内容。
可用工具：list_sessions、read_messages、search_messages、get_group_members、get_session_stats、retrieve_memory。
""");
        if (evidence is { Count: > 0 })
        {
            sb.Append("\n【已检索到的相关证据（回答必须优先基于这些证据，并标注引用编号）】\n");
            foreach (var e in evidence)
            {
                var when = e.CreateTime > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(e.CreateTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "";
                var src = e.Source == "chat" ? "原文" : e.Source;
                sb.Append($"[{e.Index}] ({src}) {when} {e.SessionDisplayName} {e.SenderName}: {e.Content}\n");
            }
        }
        sb.Append("""

规则：
1. 回答必须基于工具返回的真实数据或上述证据，不要编造。
2. 引用证据时用 [N] 标注来源。
3. 证据不足时可调用工具补查（read_messages 精读、search_messages 检索、list_sessions 找会话）。
4. 给出结论 + 时间 + 会话；如果找不到，诚实说明。
""");
        return sb.ToString();
    }

    private static (long Begin, long End) DayRange(string date)
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var dt))
            return (0, 0);
        var dayStart = new DateTimeOffset(dt.Date, TimeSpan.Zero);
        return (dayStart.ToUnixTimeSeconds(), dayStart.AddDays(1).ToUnixTimeSeconds());
    }
}
