using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Core.Missions;

/// <summary>
/// 系统通知的出口（App 层实现为托盘气泡）。Core 只定义契约，方便单测断言"到底提示了什么"。
/// </summary>
public interface INotificationSink
{
    void Notify(string title, string body);
}

/// <summary>通知智能体的裁决结果：值不值得打断用户，以及那句人话。</summary>
public sealed record NotificationDigest(bool WorthNotifying, string Title, string Body)
{
    public static readonly NotificationDigest No = new(false, "", "");
}

/// <summary>
/// 专用"通知智能体"（第三阶段补充）：长期任务跑完之后，**由它决定要不要打断用户**，
/// 并把结论整理成一句人话交给系统通知。
///
/// 为什么需要一个专门的智能体、而不是见一条弹一条：
///   后台任务大多数轮次是"无新增/空转"（实测那个每 40 秒的任务，一半以上的记录是"跳过"），
///   见一条弹一条等于把通知做成骚扰。这里先用确定性规则筛掉明显没信息量的（跳过/基线），
///   剩下才交给模型判断——判"值得"的同时它顺手把结论压缩成一句标题+一句话正文。
///
/// 模型不可用（或省额度模式）时不硬撑：退回确定性兜底（失败/终止一定提示，成功的提示正文）。
/// </summary>
public sealed class MissionNotificationAgent(IChatClient? chat = null, IAppLogger? logger = null)
{
    private readonly IChatClient? _chat = chat;

    /// <summary>
    /// 确定性前置过滤：**跳过（无新增/建基线）那种没有信息量的记录不进候选**。
    /// 这一步既省钱（少喂给模型）也少打扰，"这次没动静"不该叫醒用户。
    /// </summary>
    public static IReadOnlyList<MissionRunRecord> Candidates(IReadOnlyList<MissionRunRecord> records)
        => records.Where(r => !r.Skipped).ToList();

    /// <summary>模型不可用时的兜底裁决：失败/被终止一定提示；成功的也提示，但正文压到一句。</summary>
    public static NotificationDigest Fallback(IReadOnlyList<MissionRunRecord> candidates)
    {
        var trouble = candidates.FirstOrDefault(r => !r.Success || r.Cancelled);
        var pick = trouble ?? candidates.OrderByDescending(r => r.Summary.Length).First();
        var title = trouble is not null
            ? $"任务{(trouble.Cancelled ? "已终止" : "失败")}：{Clamp(trouble.MissionTitle, 14)}"
            : $"任务完成：{Clamp(pick.MissionTitle, 14)}";
        return new NotificationDigest(true, title, Clamp(OneLine(pick.Summary), 60));
    }

    public async Task<NotificationDigest> DecideAsync(
        IReadOnlyList<MissionRunRecord> records, CancellationToken ct = default)
    {
        var candidates = Candidates(records);
        if (candidates.Count == 0) return NotificationDigest.No;

        if (_chat is null)
            return Fallback(candidates);

        try
        {
            var result = await _chat.ChatAsync(
            [
                new ChatMessage { Role = "system", Content = SystemPrompt },
                new ChatMessage { Role = "user", Content = BuildUserPrompt(candidates) },
            ], tools: null, ct);

            var parsed = Parse(result.Content);
            if (parsed is not null)
            {
                logger?.Debug($"[通知] 裁决：值得={parsed.WorthNotifying}｜{parsed.Title}｜{parsed.Body}");
                return parsed.WorthNotifying
                    ? parsed with { Title = Clamp(parsed.Title, 20), Body = Clamp(OneLine(parsed.Body), 80) }
                    : NotificationDigest.No;
            }
            logger?.Warn("[通知] 模型没给出可解析的裁决，改用确定性兜底。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warn($"[通知] 裁决失败，改用确定性兜底：{ex.Message}");
        }

        return Fallback(candidates);
    }

    private const string SystemPrompt = """
        你是"长期任务"的通知助手：后台任务刚跑完几轮，你要判断**现在值不值得打断用户**
        （弹一条系统通知），值得的话顺手把它整理成一句人话。

        判定口径：
        - 有实际新信息（对方的新消息、新发现、新结论、需要用户拍板的事）→ 值得通知；
        - "本次无新增/已跳过"、空转、复读、纯粹的过程说明 → **不值得**，用户不需要被打断；
        - 失败、被终止、卡在某一步需要用户动手 → 值得通知，并且说清"卡在哪、要他做什么"。

        写法：
        - title 是标题（<=16 字，别用引号，别复述任务名）；
        - body 一句话（<=60 字）直接说结论或要用户做的事，不要 markdown、不要换行、不要客套；
        - 只输出 JSON，不要解释：{"worth": true, "title": "…", "body": "…"}
          （不值得时：{"worth": false, "title": "", "body": ""}）
        """;

    private static string BuildUserPrompt(IReadOnlyList<MissionRunRecord> candidates)
    {
        var sb = new System.Text.StringBuilder("刚跑完的记录（按时间正序）：\n");
        foreach (var r in candidates)
        {
            sb.Append("- [").Append(r.AtText).Append("]「").Append(r.MissionTitle).Append("」")
              .Append(r.StatusText).Append("：")
              .Append(Clamp(OneLine(r.Summary), 300)).Append('\n');
        }
        sb.Append("\n请裁决。");
        return sb.ToString();
    }

    private static NotificationDigest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var text = json.Trim();
            // 容忍 ```json 围栏
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var start = text.IndexOf('\n');
                var end = text.LastIndexOf("```", StringComparison.Ordinal);
                if (start >= 0 && end > start) text = text[(start + 1)..end].Trim();
            }
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var worth = root.TryGetProperty("worth", out var w) && w.ValueKind == JsonValueKind.True;
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "" : "";
            var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() ?? "" : "";
            if (worth && body.Trim().Length == 0) return null;    // 说值得却没说事：当没给
            return new NotificationDigest(worth, title.Trim(), body.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Clamp(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}

/// <summary>
/// 执行记录的游标：只返回"上次看过之后新产生的记录"，避免重复播报同一件事。
///
/// <see cref="PrimeToNow"/> 在启动时把游标推到当前最新——否则一开机就会把历史记录
/// 当成"刚刚发生"全部通知一遍（那是典型的自作多情）。
/// </summary>
public sealed class MissionRunCursor(IMissionRunStore store)
{
    private readonly HashSet<Guid> _seen = [];
    private DateTimeOffset _since = DateTimeOffset.MinValue;

    public void PrimeToNow()
    {
        _since = store.List(null, limit: 1).FirstOrDefault()?.At ?? DateTimeOffset.Now;
        foreach (var r in store.List(null, limit: 100)) _seen.Add(r.Id);
    }

    /// <summary>取新记录（按时间正序，便于按发生顺序播报）；取过就不再返回。</summary>
    public IReadOnlyList<MissionRunRecord> NewRecords(int limit = 50)
    {
        var fresh = store.List(null, limit: 200)
            .Where(r => r.At > _since && !_seen.Contains(r.Id))
            .OrderBy(r => r.At)
            .Take(limit)
            .ToList();

        foreach (var r in fresh) _seen.Add(r.Id);
        if (fresh.Count > 0) _since = fresh[^1].At;

        // 游标集合别无限长大：超过阈值就只留最近 100 条
        if (_seen.Count > 500)
        {
            _seen.Clear();
            foreach (var r in store.List(null, limit: 100)) _seen.Add(r.Id);
        }
        return fresh;
    }
}
