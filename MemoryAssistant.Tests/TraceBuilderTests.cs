using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Workflow;

namespace MemoryAssistant.Tests;

public class TraceBuilderTests
{
    private static WorkflowResult SampleResult()
    {
        return new WorkflowResult
        {
            Intent = new IntentResult { Intent = IntentKind.Recall, Entity = "紫薯马国敬" },
            StageEvents =
            [
                new WorkflowStageEvent("intent", "意图: Recall entity='紫薯马国敬'"),
                new WorkflowStageEvent("coarse_retrieval", "粗排 5 个候选片段"),
                new WorkflowStageEvent("evidence", "证据 12 条"),
                new WorkflowStageEvent("llm", "LLM 2 轮 30000ms"),
            ],
            AgentResult = new AgentResult
            {
                RoundCount = 2,
                TotalPromptTokens = 100,
                TotalCompletionTokens = 50,
                TotalElapsedMs = 31000,
                Rounds =
                [
                    new AgentRoundTrace
                    {
                        Round = 1, LatencyMs = 1500, PromptTokens = 60, CompletionTokens = 20,
                        FinishReason = "tool_calls",
                        ToolCalls =
                        [
                            new ToolCallTrace { Name = "read_messages", ArgumentsJson = "{\"session_id\":\"s1\"}", Success = true, LatencyMs = 300 },
                            new ToolCallTrace { Name = "search_messages", Success = false, Error = "boom", LatencyMs = 200 },
                        ],
                    },
                    new AgentRoundTrace
                    {
                        Round = 2, LatencyMs = 29000, PromptTokens = 40, CompletionTokens = 30, FinishReason = "stop",
                    },
                ],
            },
        };
    }

    [Fact]
    public void Build_NullAgentResult_EmitsErrorStep()
    {
        var result = new WorkflowResult { Intent = new IntentResult { Intent = IntentKind.Chitchat } };
        var steps = TraceBuilder.Build(result);
        Assert.Contains(steps, s => s.Category == "error" && s.Text.Contains("缺失"));
    }

    [Fact]
    public void Build_FlattensStagesRoundsTools()
    {
        var steps = TraceBuilder.Build(SampleResult());
        var cats = steps.Select(s => s.Category).ToList();

        Assert.Contains("intent", cats);
        Assert.Contains("retrieval", cats);
        Assert.Contains("evidence", cats);
        Assert.Contains("llm", cats);
        Assert.Contains("tool", cats);
        // 失败工具步骤 Level=Error
        var failed = steps.First(s => s.Text.Contains("search_messages"));
        Assert.Equal(TraceLevel.Error, failed.Level);
        // 成功工具为 Info
        var ok = steps.First(s => s.Text.Contains("read_messages"));
        Assert.Equal(TraceLevel.Info, ok.Level);
        // 含合计行
        Assert.Contains(steps, s => s.Text.Contains("合计") && s.Text.Contains("100+50"));
    }

    [Fact]
    public void Build_TokenSummary_WhenZeroShowsNotCollected()
    {
        var result = new WorkflowResult
        {
            Intent = new IntentResult { Intent = IntentKind.Stats },
            AgentResult = new AgentResult { RoundCount = 1, Rounds = [new AgentRoundTrace { Round = 1 }] },
        };
        var steps = TraceBuilder.Build(result);
        Assert.Contains(steps, s => s.Text.Contains("未统计"));
    }

    [Fact]
    public void Build_EmptyResult_NoThrow()
    {
        var steps = TraceBuilder.Build(new WorkflowResult());
        Assert.NotNull(steps);
        Assert.Contains(steps, s => s.Category == "error");
    }

    [Fact]
    public void Build_FailedStageEvent_MarksError()
    {
        var result = new WorkflowResult
        {
            StageEvents = [new WorkflowStageEvent("precise_read", "精读失败 s1: boom")],
        };
        var steps = TraceBuilder.Build(result);
        var ev = steps.First(s => s.Category == "retrieval");
        Assert.Equal(TraceLevel.Error, ev.Level);
    }
}
