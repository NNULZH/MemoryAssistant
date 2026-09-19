using CommunityToolkit.Mvvm.ComponentModel;
using MemoryAssistant.Core.Workflow;

namespace MemoryAssistant.App.ViewModels;

/// <summary>
/// 工作流页 ViewModel。
///
/// 这里不做 DAG 编辑器（计划书 §15 明确"展示 Workflow 即可"）：
/// 左边画清楚"这个 Agent 的固定阶段 + 每阶段由谁负责"，右边回放"最近一次在对话页真实发生的步骤"。
/// 阶段定义来自 Core 的 <see cref="WorkflowCatalog"/>——和 Agent 回答"我有哪些工作流"用的是同一份
/// （规范 §41：Agent 看到的软件世界和用户看到的软件世界应该是同一个）。
/// </summary>
public partial class WorkflowViewModel : ObservableObject
{
    public WorkflowViewModel(ChatViewModel chat) => Chat = chat;

    /// <summary>对话页的 ViewModel：工作流页直接复用它最近一次的执行步骤（同一份数据，不另存一份）。</summary>
    public ChatViewModel Chat { get; }

    /// <summary>固定阶段（与 Agent 的真实装配对应，不是画着好看的示意图）。</summary>
    public IReadOnlyList<WorkflowStageInfo> Nodes => WorkflowCatalog.Stages;
}
