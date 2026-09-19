using System.Text.Json;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Infrastructure;
using MemoryAssistant.Infrastructure.Missions;

namespace MemoryAssistant.App.Missions;

/// <summary>
/// 追踪型任务的增量探测器（V3.6）：通过 Python Bridge 读真实微信数据，回答
/// "自上次执行以来，追踪对象有没有新消息？"，并把新消息内容作为增量上下文交给执行器。
///
/// 会话解析用 list_sessions(keyword)（匹配 wxid / 会话摘要 / 最后发言人昵称），
/// 因此追踪对象建议填"能出现在会话摘要里的词"（对方昵称、群名、wxid）。
/// 解析不到或没有新消息时返回 Skip —— 任务不会空转，也不会重复总结同一批内容。
/// </summary>
public sealed class WeChatMissionProbe(AppServices services) : IMissionProbe
{
    private const int MaxContextMessages = 30;

    private readonly AppServices _services = services;

    public async Task<MissionProbeResult> ProbeAsync(MissionDefinition mission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mission.Target))
            return MissionProbeResult.Run("未指定追踪对象，按任务目标整体执行");

        var bridge = _services.Bridge;

        // 1) 解析会话
        var sessions = await bridge.RequestAsync("list_sessions", new Dictionary<string, object?>
        {
            ["keyword"] = MissionTargetAliases.Resolve(mission.Target.Trim()),
            ["limit"] = 5,
        }, timeoutSeconds: 30, ct: ct);

        if (!sessions.Success || sessions.Data is not JsonElement list ||
            list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            // V4.1 主题型追踪："留意就业信息"这类对象**不是会话**，而是一个关键词。
            // 先按关键词跨会话搜"自上次执行以来提到它的新消息"；搜不到（或 Bridge 不支持）再退回原来的提示。
            if (await ProbeByTopicAsync(mission, ct) is { } topic) return topic;

            // 无法命中时给几个最近会话做参考（追踪对象要用"能被会话命中的词"：wxid/群 id/摘要里的词）
            var hint = await DescribeRecentSessionsAsync(ct);
            return MissionProbeResult.Skip(
                $"未能解析到会话「{mission.Target}」（请改用 wxid / 群 id / 会话摘要里出现的词）{hint}");
        }

        var first = list[0];
        var sessionId = first.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
        if (sessionId.Length == 0) return MissionProbeResult.Skip("会话解析结果缺少 username");

        // 2) 读取"自上次执行以来"的消息（首次执行为建立基线，不向前追溯全量历史）
        long begin = mission.LastRunAt?.ToUnixTimeSeconds() ?? 0;
        var read = await bridge.RequestAsync("read_messages", new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["limit"] = 80,
            ["begin"] = begin,
        }, timeoutSeconds: 30, ct: ct);

        if (!read.Success || read.Data is not JsonElement messages || messages.ValueKind != JsonValueKind.Array)
            return MissionProbeResult.Skip($"读取会话消息失败：{read.Error ?? "未知错误"}");

        // 3) 只关心"对方发来的"、且不是系统噪音的新消息（自己发的不必提醒自己）
        var lines = new List<string>();
        var count = 0;
        foreach (var m in messages.EnumerateArray())
        {
            if (IsSelf(m)) continue;
            var content = ContentOf(m);
            if (content.Length == 0 || ContentNoiseFilter.IsNoise(content)) continue;

            count++;
            if (lines.Count < MaxContextMessages) lines.Add(FormatLine(m));
        }

        if (count == 0)
            return MissionProbeResult.Skip(mission.LastRunAt is null ? "尚无历史消息可作基线" : "目标会话没有对方的新消息");

        return MissionProbeResult.Run(
            $"「{mission.Target}」新增 {count} 条",
            count,
            string.Join("\n", lines));
    }

    /// <summary>
    /// 主题型追踪（V4.1）：追踪对象不是会话时，交给 <see cref="TopicScanService"/> 扫一轮——
    /// 模型把主题扩成相关词（"就业信息"→校招/内推/招聘），跨会话广扫后按命中密度排优先队列，
    /// 热点会话再深读一遍新消息；画像（相关词 + 会话命中累计）写回任务，跨轮次越用越准。
    /// 返回 <c>null</c> 表示这条路径不适用（未装配扫描服务），调用方仍走"未能解析到会话"的提示。
    /// </summary>
    private async Task<MissionProbeResult?> ProbeByTopicAsync(MissionDefinition mission, CancellationToken ct)
    {
        var scanner = _services.TopicScan;
        if (scanner is null) return null;

        var topic = mission.Target.Trim();
        long begin = mission.LastRunAt?.ToUnixTimeSeconds() ?? 0;

        TopicScanOutcome outcome;
        try
        {
            outcome = await scanner.ScanAsync(topic, mission.WatchProfile, begin, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MissionProbeResult.Skip($"主题扫描失败：{ex.Message}");
        }

        mission.RecordWatchProfile(outcome.ProfileJson);   // 画像回写（调度器每个自动轮次都会持久化）

        if (outcome.Hits.Count == 0)
        {
            var words = string.Join("/", outcome.Keywords);
            return MissionProbeResult.Skip(mission.LastRunAt is null
                ? $"尚未搜到提到「{topic}」的消息（相关词：{words}），暂时没有可作基线的内容"
                : $"没有新消息提到「{topic}」（相关词：{words}）");
        }

        return MissionProbeResult.Run(outcome.Note, outcome.Hits.Count, outcome.Context);
    }

    private static string ContentOf(JsonElement message)
        => message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

    /// <summary>一行追踪结果。</summary>
    private static string FormatLine(JsonElement message)
    {
        var who = message.TryGetProperty("display_name", out var d) ? d.GetString() ?? "" : "";
        var ts = message.TryGetProperty("create_time", out var t) && t.TryGetInt64(out var sec) ? sec : 0;
        var when = ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("MM-dd HH:mm") : "--";
        return $"[{when}] {who}: {ContentOf(message)}";
    }

    /// <summary>列几个最近会话，作为"追踪对象该怎么填"的提示（仅解析失败时调用）。</summary>
    private async Task<string> DescribeRecentSessionsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _services.Bridge.RequestAsync("list_sessions",
                new Dictionary<string, object?> { ["limit"] = 5 }, timeoutSeconds: 20, ct: ct);
            if (!resp.Success || resp.Data is not JsonElement arr || arr.ValueKind != JsonValueKind.Array)
                return "";

            var names = new List<string>();
            foreach (var s in arr.EnumerateArray())
            {
                var user = s.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
                if (user.Length > 0) names.Add(user);
            }
            return names.Count > 0 ? $"；最近会话可作为参考：{string.Join("、", names)}" : "";
        }
        catch
        {
            return "";
        }
    }

    private static bool IsSelf(JsonElement message)
    {
        if (!message.TryGetProperty("is_self", out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.GetInt64() != 0,
            JsonValueKind.String => v.GetString() is "1" or "true",
            _ => false,
        };
    }
}
