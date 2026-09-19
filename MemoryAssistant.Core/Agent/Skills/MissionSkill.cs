using MemoryAssistant.Core.Missions;

namespace MemoryAssistant.Core.Agent.Skills;

/// <summary>
/// 任务编排 Skill（对话式"需求 → 可用工作流"）：
/// 把用户一句话（如"帮我追踪和文件传输助手的聊天，有新消息就总结给我"）解析成结构化任务草案，
/// 交由 UI 弹出确认卡；**确认前不落库、不调度**（K16 安全边界：写操作必须人工点头）。
/// 规则解析不出时诚实说明，不猜、不硬造任务。
/// </summary>
public sealed class MissionSkill : IAgentSkill
{
    public string Name => "mission";
    public string Description => "把一句话需求编排成可执行任务（追踪会话/定时汇总），确认后创建";

    public Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
    {
        // 用**用户原话**解析，不要用规划器重述过的 goal：
        // 实测规划器把"每30秒和文件传输助手智能聊天…"重述成"创建一个每 30 秒轮询文件传输助手会话、由 AI 判断…"，
        // 于是"和X智能聊天"这个句式没了，对象被抠成结尾的"任务"（同 ActionSkill 踩过的坑）。
        var raw = string.IsNullOrWhiteSpace(request.OriginalQuery) ? request.Query : request.OriginalQuery;
        var draft = MissionIntentParser.TryParse(raw);
        if (draft is null)
        {
            return Task.FromResult(new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = false,
                Summary = "未能识别为任务编排请求。",
            });
        }

        return Task.FromResult(new SkillResult
        {
            Skill = Name,
            Success = true,
            Sufficient = true,
            Summary = $"生成任务草案：{draft.Title}（{draft.TriggerText}）",
            Draft = "我已把这句话整理成一个可长期执行的任务，确认后就会创建并在后台按周期巡检。",
            PendingMission = draft,
        });
    }
}
