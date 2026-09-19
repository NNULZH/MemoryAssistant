using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.App.Display;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Core.Workflow;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.ViewModels;

/// <summary>一条证据（Evidence Explorer / 跳转定位用）。</summary>
public sealed record EvidenceItem(
    int Index,
    string SessionName,
    string Time,
    string Sender,
    string Content)
{
    /// <summary>会话 id + 时间：点卡片"跳到对应聊天位置"要用（TopicsView 等旧调用点可不填）。</summary>
    public string SessionId { get; init; } = "";
    public long CreateTime { get; init; }
    /// <summary>记录所属日期（yyyy-MM-dd）。</summary>
    public string Date { get; init; } = "";
}

/// <summary>
/// 一次工具调用（UI 单独凸出展示）。刻意做成独立对象而不是往思考文本里塞一行：
/// 思考是"模型说它想干什么"，这里是"它实际调了什么、拿到什么"——两者必须能分开看，
/// 否则模型只在思考里说"我去查一下X"、却一次工具都没调，用户完全看不出来。
/// </summary>
public partial class ToolCallItem : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>running | ok | failed</summary>
    [ObservableProperty]
    private string _state = "running";

    /// <summary>参数摘要（"keyword=张晓明, private_only=true"）。</summary>
    [ObservableProperty]
    private string _args = "";

    /// <summary>结果摘要（成功给内容，失败给原因）。</summary>
    [ObservableProperty]
    private string _result = "";

    public bool IsRunning => State == "running";
    public bool IsOk => State == "ok";
    public bool IsFailed => State == "failed";

    /// <summary>一行标题："find_sessions(keyword=张晓明)"。</summary>
    public string Headline => Args.Length == 0 ? Name : $"{Name}({Args})";

    /// <summary>状态标记位（配 <c>ToolStateToBrushConverter</c> 上色）。</summary>
    public string Glyph => State switch { "ok" => "✓", "failed" => "✕", _ => "○" };

    partial void OnStateChanged(string value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(Glyph));
    }

    partial void OnArgsChanged(string value) => OnPropertyChanged(nameof(Headline));
}

/// <summary>一条聊天消息（UI 展示）。属性可写：流式回答要边收边刷。</summary>
public partial class ChatItem : ObservableObject
{
    public ChatItem()
    {
        // 「工具调用」面板的显隐绑在 Tools 这个集合**本身**上，而集合引用不会变——
        // 不主动通知的话，绑定只会在首次求值（集合为空）时算一次，面板永远不出现（踩过）。
        Tools.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Tools));
    }

    public required string Role { get; init; }      // user | assistant | meta | mission

    /// <summary>正文（流式时被增量追加）。</summary>
    [ObservableProperty]
    private string _content = "";

    /// <summary>流式过程中的实时状态（"正在调用工具查聊天记录…"）；有正文后清空。</summary>
    [ObservableProperty]
    private string? _status;

    /// <summary>
    /// 流式思考（先于答案出现）。只保留尾部一段：模型思考常有几千字，全量堆在气泡里没法看，
    /// 完整版在下面的「思考过程」里。
    /// </summary>
    [ObservableProperty]
    private string? _thinking;

    /// <summary>本次运行实际发生的工具调用（实时追加，不是事后展开的日志）。</summary>
    public ObservableCollection<ToolCallItem> Tools { get; } = [];

    /// <summary>意图/证据/耗时摘要（流式结束后补上，所以必须可通知）。</summary>
    [ObservableProperty]
    private string? _meta;

    [ObservableProperty]
    private IReadOnlyList<EvidenceItem>? _evidenceItems;

    /// <summary>P8 Agent Trace（思考过程面板）。</summary>
    [ObservableProperty]
    private IReadOnlyList<TraceStep>? _traceSteps;

    /// <summary>任务草案（对话式编排）：非空时该气泡渲染"任务确认卡"。</summary>
    public MissionDraft? MissionDraft { get; init; }
}

/// <summary>回忆问答页 ViewModel：接入 WorkflowEngine。</summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// <summary>工具调用台账（展示用）：本次对话里每次工具调用都留一条，「工具」页据此显示"最近调用"。</summary>
    private readonly ToolCallLog _toolLog;

    public ChatViewModel(AppServices services, ToolCallLog toolLog)
    {
        _services = services;
        _toolLog = toolLog;
        // 空状态引导的显隐跟随消息数（集合变化不会自动刷新属性绑定，这里显式通知）
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsChatEmpty));
        // 执行步骤集合本身引用不变 → 显式通知"面板是否有内容"
        ExecutionSteps.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasExecutionSteps));
            OnPropertyChanged(nameof(ExecutionState));
        };
    }

    public ObservableCollection<ChatItem> Messages { get; } = [];

    // ---- 「执行过程」右栏（Agent Execution）：把 Agent 的一次运行翻译成看得懂的步骤 ----

    /// <summary>本次执行的步骤时间线（理解 → 规划 → 工具/检索 → 作答 → 完成）。</summary>
    public ObservableCollection<ExecutionStep> ExecutionSteps { get; } = [];

    /// <summary>正在执行（驱动右栏的状态点与"空闲"提示）。</summary>
    [ObservableProperty]
    private bool _isExecuting;

    /// <summary>当前阶段文字（"正在检索聊天记录…"）。</summary>
    [ObservableProperty]
    private string _executionStatus = "等待提问";

    /// <summary>本次执行的汇总（"工具 3 次 · 证据 5 条 · 12.3s"）。</summary>
    [ObservableProperty]
    private string _executionSummary = "";

    /// <summary>执行面板是否已有内容（空则不显示时间线）。</summary>
    public bool HasExecutionSteps => ExecutionSteps.Count > 0;

    /// <summary>右栏状态点的状态（running/ok/pending），配 StepStateToBrushConverter 上色。</summary>
    public string ExecutionState => IsExecuting ? "running" : (HasExecutionSteps ? "ok" : "pending");

    partial void OnIsExecutingChanged(bool value) => OnPropertyChanged(nameof(ExecutionState));

    /// <summary>本轮提问（面板标题里显示"在回答什么"）。</summary>
    [ObservableProperty]
    private string _executionQuery = "";

    /// <summary>还没有任何消息 → 显示"能问什么/能派什么活"的引导。</summary>
    public bool IsChatEmpty => Messages.Count == 0;

    /// <summary>空状态引导：点一下就发问，让用户立刻明白"对话能解决什么"。</summary>
    public IReadOnlyList<string> Suggestions { get; } =
    [
        "我最近和谁聊得最多？",
        "我还有什么答应过的事没做完？",
        "总结一下我最近都在聊什么",
        "帮我追踪和某人的聊天，有新消息就总结给我",
        "给文件传输助手发一条：测试消息",
    ];

    /// <summary>任务创建成功（对话里确认后）——用于让任务页同步刷新。</summary>
    public event Action<MissionDefinition>? MissionCreated;

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// 推理细节面板开关（默认开）。
    ///
    /// 默认开是为了保住旧版的可观察性：用户能顺着看到模型怎么想、调了哪个工具、判定依据是什么。
    /// 注意区分——**推理原文是模型自己说的话，不是查证过的事实**（界面里用斜体+次级色区分）。
    /// 规范 §12 要求不把模型私有推理当"事实"展示；这里作为可选透明度开关保留，默认打开。
    /// </summary>
    [ObservableProperty]
    private bool _isTraceEnabled = true;

    /// <summary>待用户确认的任务草案（同一时刻只保留一个；确认/取消后清空）。</summary>
    [ObservableProperty]
    private MissionDraft? _pendingMission;

    public bool HasPendingMission => PendingMission is not null;

    partial void OnPendingMissionChanged(MissionDraft? value) => OnPropertyChanged(nameof(HasPendingMission));

    [RelayCommand]
    private async Task SendAsync()
    {
        var query = InputText.Trim();
        if (string.IsNullOrEmpty(query) || IsBusy) return;

        InputText = "";
        IsBusy = true;
        Messages.Add(new ChatItem { Role = "user", Content = query });

        // 右栏「执行过程」重置：本轮从"理解"开始，之后每一步都由真实的进度事件长出来
        ExecutionSteps.Clear();
        ExecutionQuery = query;
        ExecutionSummary = "";
        ExecutionStatus = "正在理解你的问题…";
        IsExecuting = true;
        OnPropertyChanged(nameof(HasExecutionSteps));
        var tracker = new ExecutionTracker(ExecutionSteps, _toolLog);
        tracker.BeginUnderstanding();

        ChatItem? streamingBubble = null;

        try
        {
            // 2.0 主路径：ConversationalAgent（自主规划→执行→换策略→带证据回答）
            if (_services.Agent is { } agent)
            {
                // 先把回答气泡放上去：状态行与正文都边跑边刷，用户不必盯着空白等 1~2 分钟。
                var bubble = new ChatItem { Role = "assistant", Status = "正在理解你的问题…" };
                streamingBubble = bubble;
                Messages.Add(bubble);

                void OnProgress(AgentProgress p)
                {
                    OnUi(() =>
                    {
                        switch (p.Kind)
                        {
                            case AgentProgressKind.Status:
                                bubble.Status = p.Text;
                                ExecutionStatus = p.Text;
                                tracker.NoteStatus(p.Text);
                                break;
                            case AgentProgressKind.ThinkingDelta:
                                // 思考先流：让用户看到模型在想什么，稍后才开始出正文
                                tracker.BeginPlanning();
                                bubble.Thinking = AppendRolling(bubble.Thinking, p.Text, ThinkingKeepChars);
                                StreamingTick?.Invoke();
                                break;
                            case AgentProgressKind.ToolCall:
                                // 气泡上的台账（原有）+ 右栏的执行步骤（课程展示用），两者同源
                                ApplyToolProgress(bubble, p);
                                if (p.Phase == "start")
                                {
                                    tracker.ToolStart(p.ToolName, p.ToolArgs);
                                    ExecutionStatus = $"正在调用工具 {p.ToolName}…";
                                }
                                else
                                {
                                    tracker.ToolEnd(p.ToolName, p.ToolSuccess, p.ToolSummary);
                                    ExecutionStatus = p.ToolSuccess
                                        ? $"工具 {p.ToolName} 已返回"
                                        : $"工具 {p.ToolName} 失败";
                                }
                                StreamingTick?.Invoke();
                                break;
                            default:
                                tracker.BeginAnswer();
                                bubble.Status = null;   // 有正文了就不再显示"正在…"
                                ExecutionStatus = "正在组织回答…";
                                bubble.Content += p.Text;
                                StreamingTick?.Invoke();
                                break;
                        }
                    });
                }

                var agentResult = await agent.RunAsync(query, CancellationToken.None, OnProgress);

                var meta = agentResult.Evidence.Count > 0
                    ? $"依据 {agentResult.Evidence.Count} 条聊天记录 · {agentResult.TotalElapsedMs / 1000.0:0.0}s"
                    : $"未读取聊天记录 · {agentResult.TotalElapsedMs / 1000.0:0.0}s";
                if (!agentResult.CompletedNormally)
                    meta += $"（未完全查清：{agentResult.EarlyStopReason}）";
                // 让用户一眼看到"模型这次想了什么、调了几次工具"（细节在气泡的「思考过程」里）
                var trace = agentResult.TaskTrace;
                if (trace is not null)
                {
                    var toolCalls = trace.Cycles.SelectMany(c => c.Steps).Sum(s => s.ToolCalls.Count);
                    var hasThinking = trace.Cycles.Any(c => !string.IsNullOrWhiteSpace(c.PlanReasoning)
                        || c.Steps.Any(s => !string.IsNullOrWhiteSpace(s.Reasoning)));
                    if (toolCalls > 0) meta += $" · 调用工具 {toolCalls} 次";
                    if (hasThinking) meta += " · 含模型思考";
                }

                // 用最终结果覆盖正文：流式中途若被兜底文案接管，气泡里可能残留半截增量
                bubble.Status = null;
                bubble.Content = agentResult.Answer;
                // 还挂着 ○ 的调用 = 没等到结果本轮就结束了：如实标出来，别留一个永远转圈的假象
                foreach (var t in bubble.Tools.Where(t => t.IsRunning))
                {
                    t.State = "failed";
                    t.Result = "未拿到结果（本轮提前结束）";
                }
                bubble.EvidenceItems = agentResult.Evidence.Select(ToEvidenceItem).ToList();
                bubble.TraceSteps = IsTraceEnabled ? TraceBuilder.Build(agentResult.TaskTrace) : null;

                // 右栏收尾：把本轮真实规模汇总出来（数字全部来自 AgentResult，不估算）
                var toolCallCount = trace?.Cycles.SelectMany(c => c.Steps).Sum(s => s.ToolCalls.Count) ?? 0;
                var skillCount = trace?.Cycles.Sum(c => c.Steps.Count) ?? 0;
                var summary = $"执行 {skillCount} 个能力 · 调用工具 {toolCallCount} 次 · 引用证据 {agentResult.Evidence.Count} 条 · {agentResult.TotalElapsedMs / 1000.0:0.0}s";
                ExecutionSummary = summary;
                tracker.CompleteRun(summary);
                ExecutionStatus = agentResult.CompletedNormally
                    ? "本轮完成"
                    : $"本轮提前结束：{agentResult.EarlyStopReason}";

                // meta 行插在回答气泡之前（原来是在后面追加，顺序看着别扭）
                Messages.Insert(Math.Max(0, Messages.Count - 1), new ChatItem { Role = "meta", Content = meta });

                // 任务编排请求：Agent 已把需求整理成"可执行任务草案"，交用户确认后才真正创建
                if (agentResult.PendingMission is { } draft)
                {
                    PendingMission = draft;
                    Messages.Add(new ChatItem
                    {
                        Role = "mission",
                        Content = "",
                        MissionDraft = draft,
                    });
                }
            }
            else
            {
                // 兜底：旧 WorkflowEngine（未装配 Agent 时）
                var workflow = _services.Workflow
                    ?? throw new InvalidOperationException("工作流未初始化（Python Bridge 可能未启动）。");

                tracker.BeginPlanning();
                var result = await workflow.RunAsync(query);

                var meta = $"意图: {result.Intent.Intent} | 证据: {result.Evidence.Count} 条 | 总耗时: {result.TotalElapsedMs:0}ms";
                if (result.StageTimings.TryGetValue("intent", out var t))
                    meta += $" | 意图路由: {t:0}ms";
                if (IsTraceEnabled && result.AgentResult is not null)
                    meta += $" | tokens {result.AgentResult.TotalPromptTokens}+{result.AgentResult.TotalCompletionTokens}";
                Messages.Add(new ChatItem { Role = "meta", Content = meta });

                var evidenceItems = result.Evidence
                    .Select(ToEvidenceItem)
                    .ToList();
                Messages.Add(new ChatItem
                {
                    Role = "assistant",
                    Content = result.Answer,
                    EvidenceItems = evidenceItems,
                    TraceSteps = IsTraceEnabled ? TraceBuilder.Build(result) : null,
                });

                // 右栏照样要有内容：用 Workflow 自带的阶段事件/阶段耗时拼出同一条时间线
                tracker.AddStages(result);
                var fallbackSummary = $"意图 {result.Intent.Intent} · 证据 {result.Evidence.Count} 条 · {result.TotalElapsedMs:0}ms";
                ExecutionSummary = fallbackSummary;
                tracker.CompleteRun(fallbackSummary);
                ExecutionStatus = "本轮完成（Workflow 引擎）";
            }
        }
        catch (Exception ex)
        {
            // 右栏如实标红：出错时别让时间线停在"进行中"，否则看着像还在跑
            tracker.FailRun(ex.Message);
            ExecutionStatus = $"出错：{ex.Message}";
            ExecutionSummary = "";

            // 已经挂上去的流式气泡别留成空白：把错误填进去，保持"一个问题一个气泡"
            if (streamingBubble is not null && string.IsNullOrWhiteSpace(streamingBubble.Content))
            {
                streamingBubble.Status = null;
                streamingBubble.Content = $"出错了：{ex.Message}";
            }
            else
            {
                Messages.Add(new ChatItem
                {
                    Role = "meta",
                    Content = $"出错了：{ex.Message}",
                });
            }
        }
        finally
        {
            IsBusy = false;
            IsExecuting = false;
            OnPropertyChanged(nameof(HasExecutionSteps));
        }
    }

    /// <summary>流式正文有增量（视图据此自动滚到底）。</summary>
    public event Action? StreamingTick;

    /// <summary>
    /// 把一条工具调用进度落到气泡的「工具调用」列表上。
    /// start 追加一条（○ 进行中），finish 认领**最近一条同名且还在进行中**的记录补结果——
    /// 必须按名字就近认领：同一个工具在一次任务里会被连续调用多次（find_sessions 常连着调三四次），
    /// 只记"最后一条"会把结果张冠李戴。
    /// </summary>
    private static void ApplyToolProgress(ChatItem bubble, AgentProgress p)
    {
        if (p.Phase == "start")
        {
            bubble.Tools.Add(new ToolCallItem { Name = p.ToolName, Args = p.ToolArgs, State = "running" });
            return;
        }

        var item = bubble.Tools.LastOrDefault(t => t.Name == p.ToolName && t.IsRunning)
                   ?? bubble.Tools.LastOrDefault(t => t.Name == p.ToolName);
        if (item is null)
        {
            // 只收到 finish（少见：回调被丢/事件重排）→ 补一条完整记录，别吞掉
            bubble.Tools.Add(new ToolCallItem { Name = p.ToolName, State = "running" });
            item = bubble.Tools[^1];
        }
        item.State = p.ToolSuccess ? "ok" : "failed";
        item.Result = p.ToolSummary;
    }

    /// <summary>流式思考只保留尾部这么多字符（模型思考常有几千字）。</summary>
    private const int ThinkingKeepChars = 900;

    /// <summary>滚动式拼接：超长时只留尾巴，避免气泡被思考撑爆。</summary>
    private static string AppendRolling(string? current, string delta, int keep)
    {
        var text = (current ?? "") + delta;
        return text.Length <= keep ? text : "…" + text[^keep..];
    }

    /// <summary>流式增量回来的线程不一定是 UI 线程，统一切回 UI 线程再改属性。</summary>
    private static void OnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action);
    }

    /// <summary>点卡片跳转要用到会话 id / 时间，这里一并带上（rag 片段没有精确时间时用日期兜底）。</summary>
    private static EvidenceItem ToEvidenceItem(Core.Agent.Evidence e) => new(
        e.Index,
        string.IsNullOrWhiteSpace(e.SessionDisplayName) ? e.SessionId : e.SessionDisplayName,
        e.CreateTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(e.CreateTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : e.Date,
        e.SenderName,
        e.Content)
    {
        SessionId = e.SessionId,
        CreateTime = e.CreateTime,
        Date = e.Date,
    };

    [RelayCommand]
    private void NewChat()
    {
        Messages.Clear();
        PendingMission = null;
    }

    /// <summary>点击示例问题：直接提问（省去用户想"能问什么"）。</summary>
    [RelayCommand]
    private async Task UseSuggestionAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsBusy) return;
        InputText = text;
        await SendAsync();
    }

    /// <summary>确认创建任务：写入任务目录 + 持久化 + 纳入调度（对话式编排的落地点）。</summary>
    [RelayCommand]
    private void ConfirmMission()
    {
        if (PendingMission is not { } draft) return;
        var composer = _services.MissionComposer;
        if (composer is null)
        {
            Messages.Add(new ChatItem { Role = "meta", Content = "任务编排未装配，无法创建（请检查服务初始化）。" });
            return;
        }

        var created = composer.Create(draft, start: true);
        PendingMission = null;
        Messages.Add(new ChatItem
        {
            Role = "meta",
            Content = created.Trigger == MissionTriggerKind.Manual
                ? $"已创建任务「{created.Title}」。可在「任务」页立即执行一次。"
                : $"已创建任务「{created.Title}」，{MissionText.Interval(created.IntervalMinutes, created.IntervalSeconds)}巡检一次，"
                  + "已在后台开始运行（可在「任务」页查看进度）。",
        });
        MissionCreated?.Invoke(created);
    }

    /// <summary>取消草案：不写库、不调度（默认安全路径）。</summary>
    [RelayCommand]
    private void CancelMission()
    {
        if (PendingMission is null) return;
        PendingMission = null;
        Messages.Add(new ChatItem { Role = "meta", Content = "已取消，未创建任何任务。" });
    }

    /// <summary>
    /// 把一次运行翻译成右栏的时间线（理解 → 规划 → 工具/检索 → 作答 → 完成）。
    ///
    /// 只消费已经存在的事件（AgentProgress / WorkflowResult），不改变 Agent 的任何行为：
    /// 每一步都是"真的发生了才出现"，耗时是本机实测的墙上时钟（不是模型编的），
    /// 拿不到就不显示——宁可少一行，也不要让老师看到一个假数字。
    /// </summary>
    private sealed class ExecutionTracker
    {
        private readonly ObservableCollection<ExecutionStep> _steps;
        private readonly ToolCallLog _log;
        private readonly Stopwatch _run = Stopwatch.StartNew();
        private readonly Dictionary<ExecutionStep, Stopwatch> _timers = [];
        private ExecutionStep? _understanding;
        private ExecutionStep? _planning;
        private ExecutionStep? _activity;
        private ExecutionStep? _answer;
        private string _lastActivity = "";
        private int _seq;

        public ExecutionTracker(ObservableCollection<ExecutionStep> steps, ToolCallLog log)
        {
            _steps = steps;
            _log = log;
        }

        public void BeginUnderstanding() => _understanding = Add(ExecutionStepKind.Understanding, "理解你的问题");

        /// <summary>进入规划阶段（重复调用无副作用：第一个思考增量/工具调用时切入）。</summary>
        public void BeginPlanning()
        {
            FinishUnderstanding();
            _planning ??= Add(ExecutionStepKind.Planning, "规划：决定要查什么、调哪些工具");
        }

        /// <summary>
        /// 把 Agent 的状态行翻译成一个「执行」步骤。
        /// 状态行是 Agent 自报"我现在在干什么"（"正在统计聊天量与活跃度…"），
        /// 每一次都对应一次真实的能力执行——不记下来的话，这些耗时会被算进「规划」里，
        /// 界面上就变成"规划花了 27 秒"，与事实不符。
        /// </summary>
        public void NoteStatus(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // "已规划 N 个能力：xxx" 是规划阶段的结论 → 写进规划步骤的结果里
            if (text.StartsWith("已规划", StringComparison.Ordinal))
            {
                if (_planning is not null) _planning.Result = text;
                return;
            }
            // "正在把结果讲清楚…" 属于作答阶段，不单独成步
            if (text.Contains("讲清楚", StringComparison.Ordinal)) return;
            if (!text.StartsWith("正在", StringComparison.Ordinal)) return;
            if (_planning is null) return;       // 还没进入规划（最初的"正在理解你的问题…"）
            if (text == _lastActivity) return;   // 同一状态重复推送：别把同一步记两遍

            FinishPlanning();
            FinishActivity();

            _lastActivity = text;
            var title = text.Trim().TrimEnd('…', '.');
            if (title.StartsWith("正在", StringComparison.Ordinal)) title = title[2..];
            _activity = Add(ExecutionStepKind.Activity, title);
        }

        private void FinishActivity()
        {
            if (_activity is null) return;
            _activity.Complete(Stop(_activity));
            _activity = null;
        }

        public void ToolStart(string name, string args)
        {
            BeginPlanning();
            FinishActivity();
            Add(ExecutionStepClassifier.ClassifyTool(name), $"调用 {name}", args);
            FinishPlanning();   // 计划已经落成一次真实调用，这一步就算完成了
        }

        public void ToolEnd(string name, bool success, string summary)
        {
            // 认领最近一条同名且仍在进行中的步骤：同一工具一轮里会连着调多次，只认"最后一条"会张冠李戴
            var step = _steps.LastOrDefault(s =>
                s.Title == $"调用 {name}" && s.State == "running");
            step ??= Add(ExecutionStepClassifier.ClassifyTool(name), $"调用 {name}");

            var ms = Stop(step);
            if (success) step.Complete(ms, summary);
            else step.Fail(ms, summary.Length > 0 ? summary : "调用失败");
            _log.Record(name, step.Args, summary, success, ms);
        }

        public void BeginAnswer()
        {
            FinishUnderstanding();
            FinishPlanning();
            FinishActivity();
            _answer ??= Add(ExecutionStepKind.Answer, "组织回答");
        }

        /// <summary>兜底路径（未装配 Agent 时）：用 Workflow 的阶段事件拼出同一条时间线。</summary>
        public void AddStages(WorkflowResult result)
        {
            foreach (var ev in result.StageEvents)
            {
                var kind = ev.Stage switch
                {
                    "intent" or "chitchat" => ExecutionStepKind.Understanding,
                    "llm" => ExecutionStepKind.Answer,
                    "coarse_retrieval" or "precise_read" or "scan_commitments" or "fallback" => ExecutionStepKind.Retrieval,
                    _ => ExecutionStepKind.Planning,
                };
                var step = Add(kind, StageTitle(ev.Stage), "");
                if (result.StageTimings.TryGetValue(ev.Stage, out var ms)) step.Complete((long)ms, ev.Detail);
                else step.Complete(0, ev.Detail);
            }
        }

        public void CompleteRun(string summary)
        {
            FinishUnderstanding();
            FinishPlanning();
            FinishActivity();
            if (_answer is not null)
            {
                var ms = Stop(_answer);
                _answer.Complete(ms);
                _answer = null;
            }
            var done = new ExecutionStep(++_seq, ExecutionStepKind.Complete, "完成") { Result = summary };
            done.Complete(_run.ElapsedMilliseconds);
            _steps.Add(done);
        }

        public void FailRun(string reason)
        {
            foreach (var s in _steps.Where(s => s.State == "running").ToList())
            {
                var ms = Stop(s);
                s.Fail(ms, reason);
            }
        }

        private ExecutionStep Add(ExecutionStepKind kind, string title, string args = "")
        {
            var step = new ExecutionStep(++_seq, kind, title) { Args = args };
            _timers[step] = Stopwatch.StartNew();
            _steps.Add(step);
            return step;
        }

        private long Stop(ExecutionStep step)
            => _timers.TryGetValue(step, out var sw) ? sw.ElapsedMilliseconds : 0;

        private void FinishUnderstanding()
        {
            if (_understanding is null) return;
            _understanding.Complete(Stop(_understanding));
            _understanding = null;
        }

        private void FinishPlanning()
        {
            if (_planning is null) return;
            _planning.Complete(Stop(_planning));
            _planning = null;
        }

        private static string StageTitle(string stage) => stage switch
        {
            "intent" => "识别意图",
            "chitchat" => "判断为闲聊",
            "coarse_retrieval" => "粗检索（向量 + 全文）",
            "precise_read" => "精读命中的会话原文",
            "scan_commitments" => "扫描承诺/待办",
            "evidence" => "整理证据",
            "fallback" => "降级检索",
            "llm" => "生成回答",
            _ => stage,
        };
    }
}
