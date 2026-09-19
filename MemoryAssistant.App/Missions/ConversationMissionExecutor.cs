using System.Diagnostics;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.Missions;

/// <summary>
/// 任务执行器（V3.2 / V3.6 / V3.7）：把任务目标交给独立的 ConversationalAgent 自主完成，
/// 产出摘要 + 证据数 + 执行轨迹（TaskTrace），供任务日志与 UI 展示。
/// 每个任务用独立 Agent 实例，避免任务间上下文串扰。
/// 追踪型任务（V3.6）会把"本次新增内容"并入任务输入，让 Agent 只针对增量作答。
/// 自动回复任务（V3.7）不走 Agent，而是交给 AutoReplySubAgent（抓新消息→生成→发送→只回关键信息）。
/// 增量轮次（V4.1）也不走 Agent：新内容已经在 probe 手里，直接让模型挑相关的、组织成答案
/// （走 Agent 会被规划器把"新增一个loop"这类原话又当成建任务指令）。
/// </summary>
public sealed class ConversationMissionExecutor(AppServices services) : IMissionExecutor
{
    private readonly AppServices _services = services;

    public async Task<MissionExecutionResult> ExecuteAsync(
        MissionDefinition mission, MissionProbeResult? probe, CancellationToken ct)
    {
        try
        {
            var r = await RunOnceCoreAsync(mission, probe, ct);
            // 统一收尾判定（见 MissionExecutionResult.Cancelled 的说明）：用户点了「终止执行」时，
            // 子智能体常常自己收了尾并返回一个"正常"结果（"这次没能查完：用户取消"）——
            // 那会让卡片与账本把一次被掐断的执行说成"完成"。这里按 token 状态如实改写。
            return ct.IsCancellationRequested && !r.Cancelled
                ? r with { Cancelled = true, Success = false }
                : r;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 子智能体把取消抛了出来：同样如实记一条"已终止"，而不是什么都没有
            return new MissionExecutionResult
            {
                Success = false,
                Cancelled = true,
                Summary = "本轮已被终止（用户在任务页点了「终止执行」）",
            };
        }
    }

    private async Task<MissionExecutionResult> RunOnceCoreAsync(
        MissionDefinition mission, MissionProbeResult? probe, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // 自动回复：写操作链路自成一套（子智能体直接对接动作桥），不走"总结型"Agent。
        if (mission.Action == MissionActionKind.AutoReply)
            return await RunAutoReplyAsync(mission, ct);

        // 增量轮次（V4.1）：新内容已经躺在 probe 里了，直接组织答案，不再丢给 Agent。
        // 为什么不走 Agent：任务目标往往是用户原话（"新增一个loop，可以留意就业信息"），
        // 交给 Agent 会被规则规划器判定成"建任务"指令，然后又跑一遍编排、回一句草案文案
        // （实测：0 条证据、85ms、一个字都没提新消息）。这里让模型只干一件事——
        // 从这批新消息里挑出与主题相关的、组织成人话。
        if (probe is { NewCount: > 0 } p && p.Context.Length > 0)
            return await SummarizeIncrementAsync(mission, p, ct);

        var agent = _services.CreateAgent();
        // 任务执行喂给 Agent 的是"任务目标"这句话本身。原样丢过去，规则规划器会因为它带"每天/定期"
        // 之类的周期词，又把目标当成"建任务"指令，回一句任务草案就算跑完了
        // （实测：0 条证据、89ms、一句正事没干）。所以：
        //   1) 开头带上执行标记——规则规划器见到标记就跳过"任务编排"那一支（见 RulePlanner）；
        //   2) **明确告诉子智能体"你的能力和主智能体一样"**：实测它就是不知道自己能调工具/能发消息，
        //      于是"完全不会操作"，只回一句做不到（子智能体与主智能体共用同一份 Planner + Skill 注册表，
        //      能力本来就是齐的，缺的只是这句交底）；
        //   3) 不要建新任务（否则任务里又生任务）；写操作仍走确认闸窗，那是安全边界，不许绕。
        var prompt = $"{Core.Agent.Planner.RulePlanner.MissionExecutionMarker}{mission.Title}\n"
                   + $"任务目标：{mission.Goal}\n"
                   + "你和主智能体能力完全相同：可以检索聊天记忆、读原文、调用已注册工具，也可以发微信消息。"
                   + "需要什么就用工具**真的去做**，不要说「我做不到／我没有这个能力」。\n"
                   + "请直接按任务目标执行并给出结论；**不要创建新任务**。"
                   + "涉及发消息这类写操作时照常走确认弹窗——那是安全边界，不要试图绕开。"
                   + BuildRecentContext(mission);
        var r = await agent.RunAsync(prompt, ct);
        sw.Stop();

        var summary = string.IsNullOrWhiteSpace(r.Answer)
            ? (r.CompletedNormally ? "完成（无输出）" : $"未完成：{r.EarlyStopReason}")
            : r.Answer.Replace("\n", " ");

        return new MissionExecutionResult
        {
            Success = r.CompletedNormally,
            // **不在这里截断**：任务卡片上的短摘要由调度器负责（只截进 missions.json 的那份），
            // 执行账本要留完整正文——"任务记录可查看全文"就靠它（实测原来这里只留 200 字）。
            Summary = summary,
            EvidenceCount = r.Evidence.Count,
            TraceText = r.TaskTrace?.ToText(),
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>
    /// 把这个任务**最近几轮做过什么**喂回给子智能体（用执行账本当"会话记忆"）。
    ///
    /// 实测踩过：任务里的子智能体每轮都是**全新实例**（任务之间不串上下文，这一点是对的），
    /// 于是"每 40 秒和对面聊一句"这类任务每轮都从零开始——读到的还是同一段聊天记录，
    /// 生成的自然还是同一句话（用户原话：「记忆死活保持在 9:52，总是给出重复的回复」）。
    /// 执行账本本来就是长期记忆，这里把它当成"上一轮我说过什么"，并明确要求不要复读。
    /// </summary>
    private string BuildRecentContext(MissionDefinition mission)
    {
        var runs = _services.MissionRuns?.List(mission.Id, limit: 4) ?? [];
        if (runs.Count == 0) return "";

        var sb = new System.Text.StringBuilder("\n\n【这个任务最近几轮的执行记录（最新在前）】\n");
        foreach (var r in runs)
        {
            var body = r.Summary.Replace("\n", " ").Trim();
            if (body.Length > 300) body = body[..300] + "…";
            sb.Append("- [").Append(r.AtText).Append("] ").Append(r.StatusText).Append('：').Append(body).Append('\n');
        }
        sb.Append("**动手前先看上面的记录**：\n");
        sb.Append("1) 已经做过/已经说过的内容一律不要重复——尤其是发给同一个人的话，不要复读上一句；\n");
        sb.Append("2) 要接着对方**最新回复**往下说：先读该会话最新几条，看清对方有没有回话、回了什么；\n");
        sb.Append("3) 如果对方没有新回复、也确实没什么新内容可说，就如实说清本轮无需发送（宁可不说，也别复读）。");
        return sb.ToString();
    }

    /// <summary>
    /// 自动回复：把活儿交给子智能体，只把"关键信息"（发了/没发、发给谁、发了什么、为什么）
    /// 拿回任务日志——原始聊天上下文留在子智能体里，不进主上下文。
    /// </summary>
    private async Task<MissionExecutionResult> RunAutoReplyAsync(MissionDefinition mission, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (_services.ReplyAgent is null)
            return new MissionExecutionResult
            {
                Success = false,
                Summary = "自动回复组件未就绪（服务未初始化）",
            };

        var outcome = await _services.ReplyAgent.RunAsync(mission, ct);
        sw.Stop();
        return new MissionExecutionResult
        {
            Success = outcome.Error is null,
            Summary = Truncate(outcome.Note, 200),
            EvidenceCount = outcome.NewCount,
            Skipped = !outcome.Sent,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>
    /// 增量轮次作答（V4.1）：把"自上次执行以来的新消息"直接交给模型，让它挑出与任务主题相关的部分、
    /// 组织成人话（这就是"模型自己判相关性"）；模型不可用/省额度模式下退化成确定性摘录。
    /// 不检索、不规划、不建任务——新内容已经在手上，多绕一步只会答偏。
    /// </summary>
    private async Task<MissionExecutionResult> SummarizeIncrementAsync(
        MissionDefinition mission, MissionProbeResult probe, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var target = string.IsNullOrWhiteSpace(mission.Target) ? "目标会话" : mission.Target;
        string? summary = null;

        if (_services.Chat is { } chat && !_services.Settings.Agent.EcoMode)
        {
            try
            {
                var messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = IncrementSystemPrompt },
                    new()
                    {
                        Role = "user",
                        Content = $"任务：{mission.Title}\n关注对象/主题：{target}\n"
                                + $"自上次执行以来「{target}」有 {probe.NewCount} 条新消息：\n{probe.Context}",
                    },
                };
                var result = await chat.ChatAsync(messages, tools: null, ct);
                summary = result.Content?.Trim();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _services.Logger.Warn($"[任务] 增量作答失败，改用确定性摘录：{ex.Message}");
            }
        }

        summary = string.IsNullOrWhiteSpace(summary)
            ? $"「{target}」新增 {probe.NewCount} 条：{probe.Context.Replace("\n", " ⏎ ")}"
            : summary!.Replace("\n", " ");

        sw.Stop();
        return new MissionExecutionResult
        {
            Success = true,
            // 同样不在这里截断：完整正文进执行账本（任务卡片上的短摘要由调度器负责截）
            Summary = summary,
            EvidenceCount = probe.NewCount,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>增量作答的系统提示：只根据给定新消息作答，不相关就明说，别把任务目标本身当答案。</summary>
    private const string IncrementSystemPrompt = """
        你是"长期任务"的执行器。用户委托你持续盯着某个对象/主题，你现在拿到的是**自上次执行以来的新消息**。
        要求：
        1. 只根据给你的这批新消息作答，不要调用记忆检索，也不要再问用户要什么。
        2. 挑出与"关注对象/主题"相关的内容，按会话/人归类，简洁列出要点、时间、待办；不相关的直接忽略。
        3. 如果这批内容与主题无关，就一句"本批内容与<主题>无关"。
        4. 不要复述任务名称或任务目标本身（例如"我已把这句话整理成一个任务"这种话绝对不要出现）。
        5. 中文、直接给结论，不要客套，不要 markdown 标题。
        """;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
