namespace MemoryAssistant.Core.Agent.Evaluate;

/// <summary>
/// 确定性评估器（默认，零 token）：不"重新思考人生"，只做结构化判断——
/// 本轮 Sufficient → 作答；执行中断（预算/失败）→ 停（诚实上报原因）；
/// 跑完仍不足 → 建议换策略（由 Orchestrator 决定是否还有轮次/策略）。
/// </summary>
public sealed class RuleEvaluator : IEvaluator
{
    public Evaluation Evaluate(EvaluationInput input)
    {
        var run = input.Run;
        var missing = new List<string>();
        if (run.Evidence.Count == 0) missing.Add("没有拿到可引用的原文/统计证据");
        else if (!run.Sufficient) missing.Add("现有证据不足以回答问题");

        // 1. 已足够作答
        if (run.Sufficient)
        {
            var evidenceN = run.Evidence.Count;
            return new Evaluation
            {
                Sufficient = true,
                Confidence = Math.Min(0.95, 0.55 + evidenceN * 0.1),
                Missing = [],
                RecommendedAction = "answer",
                Reason = evidenceN > 0
                    ? $"本策略拿到 {evidenceN} 条证据，足以作答"
                    : "无需查记录，可直接作答（闲聊/统计结论）",
            };
        }

        // 2. 执行被中断 → 判断是否可恢复
        if (!run.RanToCompletion)
        {
            var roundsLeft = input.ReplanRoundsUsed < input.MaxReplanRounds;
            // 预算耗尽/取消 → 不能再动；工具失败（skill_failed/not_found）→ 可换策略绕开
            bool recoverable = (run.TerminationReason?.StartsWith("skill_failed") == true
                || run.TerminationReason?.StartsWith("skill_not_found") == true) && roundsLeft;
            return new Evaluation
            {
                Sufficient = false,
                Confidence = 0.2,
                Missing = missing,
                RecommendedAction = recoverable ? "replan" : "stop",
                Reason = recoverable
                    ? $"本策略失败（{run.TerminationReason}），换一种能力再试"
                    : run.TerminationReason ?? "执行未完成",
            };
        }

        // 3. 跑完仍不足 → 还有轮次/策略就换策略，否则停止
        var canReplan = input.ReplanRoundsUsed < input.MaxReplanRounds;
        return new Evaluation
        {
            Sufficient = false,
            Confidence = 0.25,
            Missing = missing,
            RecommendedAction = canReplan ? "replan" : "stop",
            Reason = canReplan
                ? "本策略证据不足，换一种策略再试"
                : $"重规划已达上限（{input.MaxReplanRounds}），证据仍不足，如实终止",
        };
    }
}
