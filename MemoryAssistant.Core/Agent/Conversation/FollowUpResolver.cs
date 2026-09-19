using System.Text.RegularExpressions;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Planner;

namespace MemoryAssistant.Core.Agent.Conversation;

/// <summary>追问解析结果：改写后的查询 + 意图提示（给 Planner 的 Hint，Agent 可忽略）。</summary>
public sealed record FollowUpResolution
{
    public string Query { get; init; } = "";
    public PlannerHint? Hint { get; init; }
    public string? Note { get; init; }
    public bool Changed { get; init; }
    /// <summary>
    /// 上一轮手里已经有、无需再检索就能直接用的证据（"展开第 N 条"）。
    /// 有值时上层应直接作答，而不是带着改写后的句子再查一遍——真人不会为了讲清刚说过的话
    /// 又去翻一遍档案，而且重新检索常常反而查不到（上下文已被改写）。
    /// </summary>
    public Evidence? DirectEvidence { get; init; }
}

/// <summary>
/// 确定性追问解析器（plan2 §9，零 token）：把指代/省略/展开类追问改写为
/// 完整目标，避免"第二句完全从零开始"。识别四类：
///  1) 指人代词：他/她/这个人/那人 → 用上一轮 LastPerson
///  2) 展开第 N 条：引用上一轮证据 [N] 的内容+会话，展开详情
///  3) 时间/数量延续：还有吗/再早一点/还有更多 → 基于上一轮焦点续查
///  4) 其余原样直行（仍携带上一轮焦点作 Hint）
/// </summary>
public static class FollowUpResolver
{
    private static readonly Regex PersonRef = new(@"他|她|他们|那个人|这人|此人", RegexOptions.Compiled);
    private static readonly Regex NthEvidence = new(@"第\s*([1-9]\d*)\s*条|展开第?\s*([1-9]\d*)", RegexOptions.Compiled);
    private static readonly Regex MoreRef = new(@"还有吗|还有呢|还有没有|再早一点|更早|更多|还有更多|继续说|接着", RegexOptions.Compiled);

    public static FollowUpResolution Resolve(string rawQuery, ConversationSession session)
    {
        var q = (rawQuery ?? "").Trim();
        if (q.Length == 0) return new FollowUpResolution { Query = q };

        // 1) 指人代词：他/她 …
        if (PersonRef.IsMatch(q))
        {
            var person = session.LastPerson;
            if (!string.IsNullOrEmpty(person) && !q.Contains(person, StringComparison.Ordinal))
            {
                var rewritten = Regex.Replace(q, PersonRef.ToString(), person);
                return new FollowUpResolution
                {
                    Query = rewritten,
                    Hint = new PlannerHint("recall", person, null),
                    Note = $"识别到指代（{q} → {person}），按上一轮实体续查",
                    Changed = true,
                };
            }
        }

        // 2) 展开第 N 条：上一轮证据就在手里，直接展开（不再改写句子重查一遍）
        var m = NthEvidence.Match(q);
        if (m.Success && session.Last is { } last)
        {
            var n = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            var ev = session.LastEvidenceByIndex(n);
            if (ev is not null)
            {
                var sessionName = string.IsNullOrEmpty(ev.SessionDisplayName) ? ev.SessionId : ev.SessionDisplayName;
                var snippet = ev.Content.Length <= 40 ? ev.Content : ev.Content[..40];
                return new FollowUpResolution
                {
                    Query = q,
                    Hint = new PlannerHint("recall", sessionName, null),
                    Note = $"展开第 {n} 条：{sessionName}「{snippet}」",
                    Changed = true,
                    DirectEvidence = ev,
                };
            }
        }

        // 3) 时间/数量延续
        if (MoreRef.IsMatch(q) && session.Focus is { } focus && session.Last is { } turn)
        {
            var rewritten = $"继续查：关于 {focus}，是否还有更多记录？覆盖更早的时间范围。";
            return new FollowUpResolution
            {
                Query = rewritten,
                Hint = turn.Hint,
                Note = $"基于上一轮焦点「{focus}」继续查",
                Changed = true,
            };
        }

        // 4) 原样直行，带上一轮 Hint（若有）便于 Planner 复用上下文
        return new FollowUpResolution
        {
            Query = q,
            Hint = session.Last?.Hint,
            Changed = false,
        };
    }
}
