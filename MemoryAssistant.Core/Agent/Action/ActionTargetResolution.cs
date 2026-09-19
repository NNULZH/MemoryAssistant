using MemoryAssistant.Core.Agent.Skills;

namespace MemoryAssistant.Core.Agent.Action;

/// <summary>
/// 会话目录：按人名/群名查本地聊天库里的会话（备注名、昵称、群名都算）。
///
/// 为什么写操作必须拿到它：写操作桥（OCR + 模拟键鼠）手上只有一个名字，
/// **看不到聊天库**——它既不知道这个人是否存在，也不知道该按"联系人"还是"群聊"分区去点。
/// 于是"名字没打进搜索框"这种窗口故障被当成"没有这个人"，kind 只能靠模型猜。
/// 这一口就是补上"对象核对"这件事。
/// </summary>
public interface ISessionDirectory
{
    /// <summary>按名字找会话；查不到返回空（不抛）。</summary>
    Task<IReadOnlyList<SessionHit>> FindAsync(string keyword, CancellationToken ct);
}

/// <summary>把记忆底座的会话目录接到写操作的对象核对上（生产装配用）。</summary>
public sealed class MemoryBackendSessionDirectory(IMemoryBackend backend) : ISessionDirectory
{
    public Task<IReadOnlyList<SessionHit>> FindAsync(string keyword, CancellationToken ct)
        => backend.FindSessionsAsync(keyword, privateOnly: null, limit: 8, ct);
}

/// <summary>写操作前对"目标会话"的核对结果。</summary>
public sealed record ResolvedTarget
{
    /// <summary>原始名字（用户说的 / 模型给的）。</summary>
    public string Query { get; init; } = "";

    /// <summary>目录真的查过一次（false = 没接入/超时/出错，结论不可用）。</summary>
    public bool Checked { get; init; }

    /// <summary>目录里有这个会话。</summary>
    public bool Found { get; init; }

    /// <summary>目录里的真实名字（搜索时用它，比用户口语的名字更容易命中）。</summary>
    public string Name { get; init; } = "";

    /// <summary>是群聊（决定微信搜索下拉里该点哪个分区）。</summary>
    public bool IsGroup { get; init; }

    /// <summary>该会话最后一条消息时间（unix 秒，0 = 未知）。</summary>
    public long LastTimestamp { get; init; }

    /// <summary>一句人话结论（写进工具台账 / 错误文案，供模型和用户对照事实）。</summary>
    public string Note { get; init; } = "";

    /// <summary>实际拿去搜索的名字：目录里有就用目录的名字。</summary>
    public string SearchName => Found && Name.Length > 0 ? Name : Query;
}

/// <summary>
/// 写操作的对象核对（强规则，不依赖模型自觉）：
/// 打开会话前**必须先查会话目录**，用它的结论决定"名字怎么写、该点哪个分区、失败该怪谁"。
///
/// 三条硬口径：
/// 1. 目录里有 → 用目录里的名字 + 用它判断 contact/group（不再靠模型猜分区，也不再用 auto 兜底）；
/// 2. 目录里没有 → **不据此断定"没有这个人"**（可能只是名字写法不同 / 从未聊过），照原样继续；
/// 3. 目录查不动（超时/出错/未接入）→ 明确标注"未核对"，绝不把"没查到"说成"不存在"。
/// </summary>
public sealed class WeChatTargetResolver(ISessionDirectory? directory = null, Action<string>? log = null)
{
    /// <summary>
    /// 核对超时上限：写操作不能因为"本地目录查不动"一直卡住（查不动就跳过核对，按原路继续）。
    /// python bridge 的 find_sessions 自身超时是 60 秒，那个粒度对一次发送来说太长了。
    /// </summary>
    private const int LookupTimeoutMs = 10000;

    public async Task<ResolvedTarget> ResolveAsync(string target, CancellationToken ct)
    {
        var query = (target ?? "").Trim();
        if (query.Length == 0)
            return new ResolvedTarget { Query = "", Checked = false, Note = "（没有目标名字，跳过核对）" };

        if (directory is null)
            return new ResolvedTarget { Query = query, Checked = false, Note = "（未接入会话目录，跳过了对象核对）" };

        IReadOnlyList<SessionHit> hits;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(LookupTimeoutMs);
            hits = await directory.FindAsync(query, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log?.Invoke($"[Action] 会话目录核对「{query}」超时（>{LookupTimeoutMs / 1000} 秒），跳过核对继续");
            return new ResolvedTarget { Query = query, Checked = false, Note = "（会话目录核对超时，未核对到对象，按原路继续）" };
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Action] 会话目录核对「{query}」出错，跳过核对继续：{ex.Message}");
            return new ResolvedTarget { Query = query, Checked = false, Note = $"（会话目录核对出错，未核对到对象：{ex.Message}）" };
        }

        var hit = PickBest(hits, query);
        if (hit is null)
        {
            log?.Invoke($"[Action] 会话目录核对：没有匹配「{query}」的会话（不据此断定不存在）");
            return new ResolvedTarget
            {
                Query = query,
                Checked = true,
                Found = false,
                Note = $"（核对：本地会话目录里没有匹配「{query}」的会话——可能是名字写法不同或从没聊过，"
                       + "不据此断定「没有这个人」）",
            };
        }

        var note = $"（核对：本地会话目录里有「{hit.Label}」（{(hit.IsGroup ? "群聊" : "私聊")}{When(hit.LastTimestamp)}））";
        log?.Invoke($"[Action] 会话目录核对「{query}」→ 命中「{hit.Label}」{(hit.IsGroup ? "群聊" : "私聊")}");
        return new ResolvedTarget
        {
            Query = query,
            Checked = true,
            Found = true,
            Name = hit.Label,
            IsGroup = hit.IsGroup,
            LastTimestamp = hit.LastTimestamp,
            Note = note,
        };
    }

    /// <summary>打开失败时的一句话归因：把"窗口故障"和"没这个人"分开，别让模型猜错。</summary>
    public static string FailureNote(ResolvedTarget r) => r.Found
        ? $"（核对：本地会话目录里**有**「{r.Name}」，会话是存在的——所以失败不是「没这个人」，"
          + "而是微信窗口/输入通道的问题；先调 wechat_wake_window 回正窗口再重试）"
        : r.Checked
            ? "（核对：本地会话目录里也没匹配到它——先核对名字写法；但也可能只是名字没打进搜索框，两者要分清）"
            : r.Note;

    private static string When(long ts)
    {
        if (ts <= 0) return "";
        try
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime();
            return $"，最近 {local:MM-dd HH:mm}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 从候选里挑最像的那个：先名称完全相等，再互相包含，最后退"最近活跃的"。
    /// 顺序不能反：同一个词会横跨多个会话，"最近"未必就是要找的那个人。
    /// </summary>
    private static SessionHit? PickBest(IReadOnlyList<SessionHit> hits, string query)
    {
        if (hits.Count == 0) return null;
        static string Norm(string s) => s.Trim();
        static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

        return hits.FirstOrDefault(h => Same(h.DisplayName, query))
               ?? hits.FirstOrDefault(h => h.DisplayName.Length > 0
                                           && (h.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                               || query.Contains(h.DisplayName, StringComparison.OrdinalIgnoreCase)))
               ?? hits.OrderByDescending(h => h.LastTimestamp).First();
    }
}
