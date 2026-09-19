using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Workflow;

namespace MemoryAssistant.Infrastructure.Environment;

/// <summary>
/// 环境相关工具（第三阶段 §15~§18）：让 Agent 能回答"你是谁/你有什么能力/你现在在哪/你还正常吗"。
///
/// 这些工具全部只读（Category = Context/System），不改任何状态，因此不需要人工确认。
/// 内容来自 <see cref="EnvironmentService"/> 的现读快照——和「环境」页看到的是同一份数据。
/// </summary>
public sealed class EnvironmentToolProvider(EnvironmentService environment)
{
    private readonly EnvironmentService _environment = environment;

    public void RegisterAll(ToolRegistry registry)
    {
        registry.Register(Read(ToolCategory.Context, "get_environment_context",
            "获取我当前所处的软件环境：我是谁、在哪个页面、有哪些工具/能力/工作流、有几个任务在跑、"
            + "知识库与微信是否就绪。回答'你是谁''你现在有什么能力''你现在在哪'这类自我认知问题时先调它。",
            () => _environment.Render(_environment.Capture())));

        registry.Register(Read(ToolCategory.Context, "get_ui_context",
            "获取界面当前状态：当前页面、选中的会话、选中的任务、打开中的窗口、等待确认的动作。"
            + "用户说'这个任务''刚才那个'时用它定位指代对象。",
            () => _environment.Render(_environment.Capture(), uiOnly: true)));

        registry.Register(Read(ToolCategory.Context, "list_available_tools",
            "列出我当前可以调用的全部工具（名称、分类、是否只读、用途）。",
            _environment.RenderTools));

        registry.Register(Read(ToolCategory.Context, "list_available_skills",
            "列出我内置的能力（Skills）：回忆、统计、时间线、承诺、话题、画像、工具调用、任务编排等。",
            _environment.RenderSkills));

        registry.Register(Read(ToolCategory.System, "list_available_workflows",
            "列出我的工作流：提问后会走哪些固定阶段、每个阶段由哪个组件负责。",
            () => WorkflowCatalog.ToPromptList()));

        registry.Register(new ToolDefinition
        {
            Name = "self_check",
            Description = "系统自检：逐项检查 LLM、知识库索引、Embedding、RAG 检索、微信、工具注册表、任务调度器是否正常，"
                        + "返回每项的通过/失败与说明。用户问'你还正常吗''检查一下你自己'时调用。",
            Category = ToolCategory.System,
            Parameters = [],
            ReadOnly = true,
            ExecuteAsync = async (_, ct) =>
            {
                var started = DateTime.UtcNow;
                try
                {
                    var report = await _environment.SelfCheckAsync(ct);
                    return new ToolCallResult
                    {
                        Success = true,
                        Output = EnvironmentService.RenderReport(report),
                        ElapsedMs = (DateTime.UtcNow - started).TotalMilliseconds,
                    };
                }
                catch (Exception ex)
                {
                    return new ToolCallResult { Success = false, Error = ex.Message };
                }
            },
        });
    }

    /// <summary>只读工具的样板（无参数、返回一段文本）。</summary>
    private static ToolDefinition Read(string category, string name, string description, Func<string> body) => new()
    {
        Name = name,
        Description = description,
        Category = category,
        Parameters = [],
        ReadOnly = true,
        ExecuteAsync = (_, _) =>
        {
            try { return Task.FromResult(new ToolCallResult { Success = true, Output = body() }); }
            catch (Exception ex) { return Task.FromResult(new ToolCallResult { Success = false, Error = ex.Message }); }
        },
    };
}
