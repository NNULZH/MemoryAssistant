using MemoryAssistant.Core.Integrations;

namespace MemoryAssistant.Core.Agent.Skills;

/// <summary>
/// 微信窗口 Skill（V3.3，只读）：读取微信窗口当前可见聊天，作为"实时上下文"证据。
/// 与本地历史索引互补：索引回答"过去聊过什么"，窗口读取回答"现在屏幕上是什么"。
/// 未接入/未运行微信时优雅降级（不报错、不阻断）。
/// </summary>
public sealed class WechatSkill(IWeChatWindowBridge? bridge) : IAgentSkill
{
    private readonly IWeChatWindowBridge? _bridge = bridge;

    public string Name => "wechat";
    public string Description => "读取微信窗口当前可见聊天（只读）";

    public async Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
    {
        if (_bridge is null)
        {
            return new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = false,
                Summary = "微信窗口读取未接入（可在设置中启用）。",
            };
        }

        var windows = await _bridge.ListWeChatWindowsAsync(ct);
        if (windows.Count == 0)
        {
            return new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = false,
                Summary = "未找到微信窗口（微信可能未启动）。",
            };
        }

        var read = await _bridge.ReadVisibleChatAsync(ct);
        if (!read.Success)
        {
            return new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = false,
                Summary = $"找到微信窗口但未读到聊天内容：{read.Error}",
            };
        }

        var evidence = read.Messages
            .Where(m => !ContentNoiseFilter.IsNoise(m))
            .Take(20)
            .Select(m => new Evidence
            {
                SessionDisplayName = "当前微信聊天",
                Content = m,
                Source = "微信窗口",
            })
            .ToList();

        return new SkillResult
        {
            Skill = Name,
            Success = true,
            Sufficient = evidence.Count > 0,
            Summary = evidence.Count > 0
                ? $"读取到当前微信聊天 {evidence.Count} 条消息（只读）。"
                : "微信窗口可见聊天为空或都是系统消息。",
            Evidence = evidence,
        };
    }
}
