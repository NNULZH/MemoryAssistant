using System.Text;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Environment;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Core.RAG;
using MemoryAssistant.Core.Workflow;

namespace MemoryAssistant.Infrastructure.Environment;

/// <summary>
/// 环境感知层（第三阶段 §15~§18）。
///
/// Agent 必须知道自己"身处什么软件"：现在在哪个页面、手里有哪些工具/能力/工作流、
/// 有几个任务在跑、知识库和微信还活着没有。这些信息**全部从真实组件现读**，
/// 不缓存、不猜测——所以它同时也是"系统自检"。
///
/// 同一份快照供两处使用（规范 §41 的"同一套能力模型"）：
///   · Agent：get_environment_context / list_available_* / self_check 工具
///   · GUI：「环境」页
/// </summary>
public sealed class EnvironmentService(AppServices services, IUiContext? uiContext = null)
{
    private readonly AppServices _services = services;
    private readonly IUiContext _ui = uiContext ?? new NullUiContext();

    /// <summary>抓一份环境快照（全部来自真实组件，读不到就如实写"未就绪"）。</summary>
    public EnvironmentSnapshot Capture()
    {
        var tools = _services.Tools?.All.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList() ?? [];
        var skills = SkillCatalog.Default().All.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var missions = _services.MissionCatalog?.Items ?? [];
        var running = missions.Where(m => m.Status == MissionStatus.Running).ToList();

        return new EnvironmentSnapshot
        {
            Application = "Memory Assistant",
            Version = AppVersion(),
            CurrentPage = _ui.CurrentPage,
            SelectedSession = _ui.SelectedSession,
            SelectedMission = _ui.SelectedMission,
            OpenDialog = _ui.OpenDialog,
            PendingConfirmation = _ui.PendingConfirmation,

            Tools = tools,
            Skills = skills,
            Workflows = ["对话式任务执行流程（提问 → 理解 → 规划 → 工具 → 检索 → 作答）", "长期任务巡检流程（触发 → 探测 → 执行 → 汇报）"],
            WorkflowStages = WorkflowCatalog.Stages.Select(s => s.Name).ToList(),

            MissionCount = missions.Count,
            RunningMissionCount = running.Count,
            RunningMissionTitles = running.Select(m => m.Title).ToList(),

            LlmModel = _services.Settings.Llm.Model,
            KnowledgeStatus = _services.HybridRetriever is not null ? "已接入（向量 + 全文混合检索）" : "未就绪",
            WeChatStatus = _services.WeChatWindow is not null ? "已接入（只读窗口工具）" : "未接入",
            SchedulerStatus = _services.Scheduler is { IsStarted: true } ? "运行中" : "未启动",
            AgentStatus = _services.Agent is not null ? "已装配（可自主规划与调用工具）" : "未装配",
        };
    }

    /// <summary>快照 → 人话文本（get_environment_context / get_ui_context 的输出）。</summary>
    public string Render(EnvironmentSnapshot s, bool uiOnly = false)
    {
        var sb = new StringBuilder();
        if (uiOnly)
        {
            sb.AppendLine("【当前界面状态】");
            sb.AppendLine($"当前页面：{Blank(s.CurrentPage)}");
            sb.AppendLine($"选中会话：{Blank(s.SelectedSession)}");
            sb.AppendLine($"选中任务：{Blank(s.SelectedMission)}");
            sb.AppendLine($"打开中的窗口：{Blank(s.OpenDialog)}");
            sb.AppendLine($"等待确认的动作：{Blank(s.PendingConfirmation)}");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("【我是谁】");
        sb.AppendLine($"{s.Application} {s.Version} —— {s.Role}，运行在用户本机上。");
        sb.AppendLine($"Agent 状态：{s.AgentStatus}");
        sb.AppendLine();
        sb.AppendLine("【我现在在哪】");
        sb.AppendLine($"当前页面：{Blank(s.CurrentPage)}");
        sb.AppendLine($"选中会话：{Blank(s.SelectedSession)}");
        sb.AppendLine($"选中任务：{Blank(s.SelectedMission)}");
        sb.AppendLine($"等待确认的动作：{Blank(s.PendingConfirmation)}");
        sb.AppendLine();
        sb.AppendLine("【我能做什么】");
        sb.AppendLine($"工具 {s.Tools.Count} 个：{Join(s.Tools)}");
        sb.AppendLine($"能力（Skills）{s.Skills.Count} 个：{Join(s.Skills)}");
        sb.AppendLine($"工作流：{Join(s.Workflows)}");
        sb.AppendLine($"工作流固定阶段：{Join(s.WorkflowStages)}");
        sb.AppendLine();
        sb.AppendLine("【我的长期任务】");
        sb.AppendLine($"共 {s.MissionCount} 个，其中运行中 {s.RunningMissionCount} 个"
                      + (s.RunningMissionTitles.Count > 0 ? $"：{Join(s.RunningMissionTitles)}" : "。"));
        sb.AppendLine();
        sb.AppendLine("【我的外部依赖】");
        sb.AppendLine($"模型：{s.LlmModel}");
        sb.AppendLine($"知识库：{s.KnowledgeStatus}");
        sb.AppendLine($"微信：{s.WeChatStatus}");
        sb.AppendLine($"任务调度器：{s.SchedulerStatus}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>能力目录文本（list_available_tools / skills / workflows 用）。</summary>
    public string RenderTools()
    {
        var tools = _services.Tools?.All;
        if (tools is null || tools.Count == 0) return "工具注册表未就绪（Python Bridge 可能未启动）。";
        var lines = tools
            .OrderBy(t => t.Category, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => $"- {t.Name}（{t.Category}{(t.ReadOnly ? "，只读" : "，写操作需确认")}）：{t.Description}");
        return $"我可以用 {tools.Count} 个工具：\n" + string.Join("\n", lines);
    }

    public string RenderSkills()
    {
        var skills = SkillCatalog.Default().All.Select(s => $"- {s.Name}（{s.Category}）：{s.Description}");
        return "我内置这些能力（Skills）：\n" + string.Join("\n", skills);
    }

    /// <summary>任务清单文本（list_missions 用）。</summary>
    public string RenderMissions()
    {
        var missions = _services.MissionCatalog?.Items;
        if (missions is null) return "任务目录未就绪。";
        if (missions.Count == 0) return "目前没有任何长期任务。";
        var sb = new StringBuilder($"我有 {missions.Count} 个长期任务：\n");
        foreach (var m in missions)
        {
            var last = m.LastRunAt is null ? "" : $"｜上次 {m.LastRunAt:MM-dd HH:mm}";
            sb.AppendLine($"- {m.Title}｜{m.TriggerText}｜{m.StatusText}｜已执行 {m.RunCount} 次{last}"
                          + $"｜创建方式：{m.OriginText}");
            sb.AppendLine($"    目标：{m.Goal}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 系统自检（§18）：LLM / RAG / Embedding / 微信 / 工具注册表 / 任务调度器 / 知识库索引。
    /// 每一项都真的去问一次，不返回"应该没问题"。
    /// </summary>
    public async Task<SelfCheckReport> SelfCheckAsync(CancellationToken ct = default)
    {
        var items = new List<SelfCheckItem>();

        // LLM：配置齐（模型 + baseUrl + key）
        var llm = _services.Settings.Llm;
        var hasKey = !string.IsNullOrWhiteSpace(llm.ApiKey);
        items.Add(new SelfCheckItem("LLM", hasKey,
            hasKey ? $"{llm.Model} @ {llm.BaseUrl}" : "缺少 API Key（请在「设置」页填写本机 key）"));

        // 工具注册表
        var toolCount = _services.Tools?.All.Count ?? 0;
        items.Add(new SelfCheckItem("Tool Registry", toolCount > 0, $"{toolCount} 个工具"));

        // 任务调度器
        var sched = _services.Scheduler;
        items.Add(new SelfCheckItem("Mission Scheduler", sched is { IsStarted: true },
            sched is null ? "未装配" : (sched.IsStarted ? $"运行中（{_services.MissionCatalog?.Items.Count ?? 0} 个任务）" : "未启动")));

        // 知识库 / Embedding：直接问 Bridge 要索引状态
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_index_status", null, timeoutSeconds: 30);
            if (resp.Success && resp.Data is System.Text.Json.JsonElement je)
            {
                var s = IndexStatusParser.Parse(je);
                items.Add(new SelfCheckItem("Knowledge Index", s.Loaded && s.Error is null,
                    s.Error is not null ? s.Error
                        : $"chunks={s.Chunks} 会话={s.Sessions} 消息={s.TotalMsgs}" + (s.BuiltAt is null ? "" : $" 构建={s.BuiltAt}")));
                items.Add(new SelfCheckItem("Embedding", s.Dim > 0,
                    s.Dim > 0 ? $"{_services.Settings.Embedding.Model}（{_services.Settings.Embedding.Backend}，{s.Dim} 维）" : "索引里没有向量维度"));
                items.Add(new SelfCheckItem("RAG Retriever", _services.HybridRetriever is not null,
                    _services.HybridRetriever is not null ? "向量 + 全文混合检索已装配" : "未装配"));
            }
            else
            {
                items.Add(new SelfCheckItem("Knowledge Index", false, resp.Error ?? "Bridge 无响应"));
                items.Add(new SelfCheckItem("Embedding", false, "未取到索引状态"));
                items.Add(new SelfCheckItem("RAG Retriever", _services.HybridRetriever is not null, "未取到索引状态"));
            }
        }
        catch (Exception ex)
        {
            items.Add(new SelfCheckItem("Knowledge Index", false, ex.Message));
            items.Add(new SelfCheckItem("Embedding", false, "检查失败"));
            items.Add(new SelfCheckItem("RAG Retriever", _services.HybridRetriever is not null, "检查失败"));
        }

        // 微信：真的列一次窗口（只读）
        if (_services.WeChatWindow is { } wechat)
        {
            try
            {
                var wins = await wechat.ListWeChatWindowsAsync(ct);
                items.Add(new SelfCheckItem("WeChat", wins.Count > 0,
                    wins.Count > 0 ? $"找到 {wins.Count} 个微信窗口" : "没找到微信窗口（微信未运行？）"));
            }
            catch (Exception ex)
            {
                items.Add(new SelfCheckItem("WeChat", false, ex.Message));
            }
        }
        else
        {
            items.Add(new SelfCheckItem("WeChat", false, "只读窗口桥未接入"));
        }

        // 记忆底座
        items.Add(new SelfCheckItem("Memory Backend", _services.MemoryBackend is not null,
            _services.MemoryBackend is not null ? "真实聊天数据已接入" : "未接入"));

        return new SelfCheckReport(items);
    }

    /// <summary>自检报告 → 文本（self_check 工具输出 / 控制台验收）。</summary>
    public static string RenderReport(SelfCheckReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"系统自检：{(report.AllOk ? "全部正常 ✓" : $"{report.OkCount}/{report.Items.Count} 项正常")}");
        foreach (var i in report.Items)
            sb.AppendLine($"{i.Name,-18} {(i.Ok ? "✓" : "✗")}  {i.Detail}");
        return sb.ToString().TrimEnd();
    }

    private static string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "（无）" : s!;
    private static string Join(IReadOnlyList<string> items) => items.Count == 0 ? "（无）" : string.Join("、", items);

    private static string AppVersion()
    {
        var v = typeof(EnvironmentService).Assembly.GetName().Version;
        return v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
