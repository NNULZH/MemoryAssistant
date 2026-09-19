using MemoryAssistant.Core.Agent.Planner;

namespace MemoryAssistant.Core.Agent.Skills;

/// <summary>
/// Skill 注册表：把 P12 SkillCatalog（名称+能力描述）与实现（IAgentSkill）对齐。
/// 上层拿到 catalog 规划、拿到 registry 执行。
/// </summary>
public sealed class SkillRegistry
{
    private readonly Dictionary<string, IAgentSkill> _skills = new(StringComparer.Ordinal);

    public SkillRegistry(IEnumerable<IAgentSkill> skills)
    {
        foreach (var s in skills)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) continue;
            _skills[s.Name] = s;
        }
    }

    /// <summary>
    /// 依据默认目录构建全部内置 Skill（backend 提供底层能力；wechat 为可选的只读窗口桥；
    /// extra 用于注入需要外部依赖的 Skill，如 V3.4 的 action 写操作）。
    /// </summary>
    public static SkillRegistry BuildDefault(
        IMemoryBackend backend,
        Core.Integrations.IWeChatWindowBridge? wechat = null,
        IEnumerable<IAgentSkill>? extra = null)
    {
        var skills = new List<IAgentSkill>
        {
            new BuiltInSkills.RecallSkill(backend),
            new BuiltInSkills.StatsSkill(backend),
            new BuiltInSkills.TimelineSkill(backend),
            new BuiltInSkills.CommitmentSkill(backend),
            new BuiltInSkills.TopicSkill(backend),
            new BuiltInSkills.ProfileSkill(backend),
            new WechatSkill(wechat),
            new BuiltInSkills.ChitchatSkill(),
        };
        if (extra is not null) skills.AddRange(extra);
        return new SkillRegistry(skills);
    }

    public bool TryGet(string name, out IAgentSkill skill) => _skills.TryGetValue(name, out skill!);

    public IReadOnlyCollection<IAgentSkill> All => _skills.Values;

    /// <summary>把 catalog 与 registry 对齐：缺实现的目录项列出（提示上层）。</summary>
    public static IReadOnlyList<string> MissingImplementations(SkillCatalog catalog, SkillRegistry registry)
        => catalog.All.Where(c => !registry.TryGet(c.Name, out _)).Select(c => c.Name).ToList();
}
