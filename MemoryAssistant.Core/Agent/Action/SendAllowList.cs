namespace MemoryAssistant.Core.Agent.Action;

/// <summary>
/// 发送对象白名单（第三阶段补充）：名单非空时，**只有名单内的对象能被发送**，其余一律拒绝执行。
///
/// 与确认弹窗的分工：
///   · 确认弹窗 = 用户当场拍板（每一次都可能被点歪、被惯性地"确认"过去）；
///   · 白名单   = 事前定好的死规矩（名单外的对象连弹窗都不会出现，消息根本发不出去）。
/// 用于"绝不许发错人"的场景，例如只允许给文件传输助手、家人、某个工作群发消息。
///
/// 判定用**会话目录核对后的真名**（备注名/昵称/群名都算，忽略大小写与首尾空格）：
/// 把"周斌"写进名单后，"给周斌发消息"才放行；若搜索命中的其实是"周斌的工作群"，同样会被拦住。
/// 空名单 = 不启用，行为与以前完全一致（默认值就是空）。
/// </summary>
public sealed class SendAllowList(IEnumerable<string>? names = null)
{
    private readonly List<string> _names = (names ?? [])
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Select(n => n.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>名单非空才生效（默认空 = 不启用）。</summary>
    public bool Enabled => _names.Count > 0;

    /// <summary>名单内容（日志/报错文案用）。</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>候选名字里有没有一个在名单内（名单为空则一律放行）。</summary>
    public bool Allows(params string?[] candidates)
    {
        if (!Enabled) return true;
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var trimmed = c.Trim();
            if (_names.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public string Describe() => string.Join("、", _names);

    /// <summary>被拒绝时的统一文案（Skill 与工具两条入口共用同一句话，口径一致）。</summary>
    public string RejectionFor(string target)
    {
        var who = string.IsNullOrWhiteSpace(target)
            ? "「当前会话」（这条指令没有指定收件人）"
            : $"「{target}」";
        return $"发送对象{who}不在发送白名单内，已拒绝执行，未发送任何消息。当前白名单：{Describe()}。"
               + "要发给它，请把名字加进 appsettings.json 的 agent.sendAllowList（改完重启应用）——"
               + "名单非空时，只有名单内的对象能被发送。";
    }
}
