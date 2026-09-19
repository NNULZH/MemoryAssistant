using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Conversation;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Features.Commitment;
using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.PythonBridge;
using MemoryAssistant.Core.RAG;
using MemoryAssistant.Core.Workflow;
using MemoryAssistant.Infrastructure.Agent;
using MemoryAssistant.Infrastructure.Memory;
using MemoryAssistant.Infrastructure.PythonBridge;
using MemoryAssistant.Infrastructure.RAG;

namespace MemoryAssistant.Infrastructure;

/// <summary>
/// 应用服务装配：组装 Bridge / ChatClient / RAG / WorkflowEngine。
/// 供 WPF 视图模型使用（保持 UI 与 Agent 解耦，对应计划原则 1）。
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppServices(AppSettings settings, IAppLogger logger, string? toolsDirectory = null, string? missionsFilePath = null)
    {
        Settings = settings;
        Logger = logger;
        _toolsDirectory = toolsDirectory;
        _missionsFilePath = missionsFilePath;
        Bridge = new PythonBridgeClient(
            settings.PythonBridge.PythonExePath,
            settings.PythonBridge.BridgeScriptPath,
            logger,
            settings.PythonBridge.IdleTimeoutSeconds);
    }

    private readonly string? _toolsDirectory;
    private readonly string? _missionsFilePath;

    public AppSettings Settings { get; }
    public IAppLogger Logger { get; }
    public IPythonBridge Bridge { get; }

    public IChatClient? Chat { get; private set; }
    public IRetriever? HybridRetriever { get; private set; }
    public WorkflowEngine? Workflow { get; private set; }
    public ToolRegistry? Tools { get; private set; }
    /// <summary>V3.5 工具热注册中心（外部清单可随时上/下架工具）。</summary>
    public MemoryAssistant.Core.Agent.Tools.ToolHub? ToolHub { get; private set; }
    /// <summary>V3.5 工具清单仓库（监听 data/tools/*.tool.json 变化）。</summary>
    public JsonToolManifestStore? ToolManifests { get; private set; }
    /// <summary>2.0 Agent（P20 产品主入口）：Planner + 7 Skills + 真实 BridgeMemoryBackend。</summary>
    public ConversationalAgent? Agent { get; private set; }
    /// <summary>真实记忆底座（读原文/会话目录）：供"点证据卡片跳到对应聊天位置"这类 UI 直接用。</summary>
    public Core.Agent.Skills.IMemoryBackend? MemoryBackend { get; private set; }
    /// <summary>任务持久化仓库（V4.0：任务与对话共用同一份目录，避免"两套任务"）。</summary>
    public Missions.JsonMissionRepository? MissionRepository { get; private set; }
    /// <summary>任务目录（对话编排出的任务与任务页共用）。</summary>
    public Core.Missions.MissionStore? MissionCatalog { get; private set; }

    /// <summary>
    /// 任务执行账本（第三阶段补充）：每次执行落一条**不截断**的记录，跨重启保留，
    /// 支持按任务查看 / 删除单条 / 清空。任务页与"删除任务"都走它。
    /// </summary>
    public Core.Missions.IMissionRunStore? MissionRuns { get; private set; }
    /// <summary>任务调度器（由 UI 调用 Start() 后开始后台巡检）。</summary>
    public Core.Missions.MissionScheduler? Scheduler { get; private set; }
    /// <summary>任务编排写侧（对话确认后创建任务）。</summary>
    public Core.Missions.IMissionComposer? MissionComposer { get; private set; }
    /// <summary>微信写操作桥（V3.7：自动回复任务复用它发送）。</summary>
    public Core.Integrations.IWeChatActionBridge? WeChatAction { get; private set; }
    /// <summary>自动回复子智能体（抓新消息 → 生成回复 → 发送 → 只回关键信息）。</summary>
    public Missions.AutoReplySubAgent? ReplyAgent { get; private set; }
    /// <summary>
    /// 主题扫描（V4.1）：主题 → 模型扩词 → 跨会话广扫 → 按命中密度排优先队列 → 热点会话深扫。
    /// 主题型 Watch 任务（"留意就业信息"）与 agent 工具 scan_topic 共用它。
    /// </summary>
    public Missions.TopicScanService? TopicScan { get; private set; }

    /// <summary>微信只读窗口桥（第三阶段：自检要真的列一次窗口，所以这里留一份引用）。</summary>
    public Core.Integrations.IWeChatWindowBridge? WeChatWindow { get; private set; }

    /// <summary>环境感知层（第三阶段 §15）：Agent 的环境工具与 GUI 的「环境」页共用它。</summary>
    public Environment.EnvironmentService? Environment { get; private set; }

    /// <summary>
    /// Agent ↔ 用户 的唯一交互口（第三阶段补充要求 §4）。
    /// 写操作确认闸门与 request_user_confirmation 工具都走它 → 界面上只有一套确认/选择交互。
    /// </summary>
    public Core.Agent.Interaction.IUserInteraction? UserInteraction { get; private set; }

    /// <summary>
    /// 写操作授权（第三阶段补充）：用户在确认弹窗里点「以后都允许」时落一条，
    /// 之后对**同一对象**的同类写操作不再询问。落盘在 data/action-grants.json（删该条即撤销）。
    /// </summary>
    public Core.Agent.Action.IActionGrantStore? ActionGrants { get; private set; }

    /// <summary>持久化任务目录（Agent 通过任务工具改动后由它落盘）。</summary>
    public void PersistMissions() => MissionRepository?.Save(MissionCatalog?.Items ?? []);

    /// <summary>启动 Bridge 并装配 Agent 组件（耗时操作，UI 线程外调用）。</summary>
    public async Task InitializeAsync(
        CancellationToken ct = default,
        Core.Integrations.IWeChatWindowBridge? wechat = null,
        Core.Integrations.IWeChatActionBridge? wechatAction = null,
        Core.Agent.Action.IActionConfirmation? actionConfirmation = null,
        Core.Missions.IMissionExecutor? missionExecutor = null,
        Core.Missions.IMissionProbe? missionProbe = null,
        Core.Environment.IUiContext? uiContext = null,
        Core.Agent.Interaction.IUserInteraction? userInteraction = null,
        Action<string>? actionAudit = null)
    {
        // V4.0 任务子系统：先于 Bridge 就绪，保证即使 Bridge 启动失败，任务目录/调度器依然可用
        // （执行时由占位执行器如实报告不可用，而不是让整个 UI 起不来）。Start 由 UI 触发。
        MissionRepository = new Missions.JsonMissionRepository(_missionsFilePath);
        MissionCatalog = new Core.Missions.MissionStore(MissionRepository.Load());
        WeChatWindow = wechat;
        // 任务执行账本（第三阶段补充）：跨重启保留、可查看、可删除
        MissionRuns = new Missions.JsonMissionRunStore(logger: Logger);
        Scheduler = new Core.Missions.MissionScheduler(
            MissionCatalog,
            missionExecutor ?? new Missions.UnavailableMissionExecutor(),
            // 巡检 tick 可配置（V4.1）：秒级任务（每 30 秒）需要它足够小才发现得及时。
            // 原来硬编码 10 秒；配置里给了 0/负数时回落到 10 秒，避免写坏配置就再也不巡检。
            tickInterval: TimeSpan.FromSeconds(Settings.Agent.MissionTickSeconds > 0 ? Settings.Agent.MissionTickSeconds : 10),
            probe: missionProbe,
            runStore: MissionRuns);
        Scheduler.Changed += PersistMissions;
        MissionComposer = new Missions.MissionComposer(MissionCatalog, Scheduler, PersistMissions);

        var started = await Bridge.StartAsync(ct);
        if (!started) throw new InvalidOperationException("Python Bridge 启动失败");

        Chat = new DeepSeekChatClient(Settings.Llm, Logger, retryCount: Settings.Runtime.RetryCount);
        WeChatAction = wechatAction;

        // V3.7：自动回复子智能体（独立于主对话上下文；只把"关键信息"交回任务/对话）
        ReplyAgent = new Missions.AutoReplySubAgent(Bridge, Chat, wechatAction, Settings.Agent, Logger);

        // V4.1：主题扫描（模型扩词 + 优先队列 + 热点深扫），主题型 Watch 任务与 scan_topic 工具共用
        TopicScan = new Missions.TopicScanService(Bridge, Chat, Settings.Agent, Logger);

        var vector = new BridgeRagRetriever(Bridge, Logger);
        var fts = new BridgeFtsRetriever(Bridge, Logger);
        HybridRetriever = new HybridRetriever(vector, fts);

        Tools = new ToolRegistry();
        new BridgeToolProvider(Bridge, Settings.Agent).RegisterAll(Tools);

        // V3.5：工具清单热注册（data/tools/*.tool.json → ToolRegistry），并监听目录变化
        ToolHub = new MemoryAssistant.Core.Agent.Tools.ToolHub(Tools);
        ToolManifests = new JsonToolManifestStore(
            _toolsDirectory ?? System.IO.Path.Combine(System.Environment.CurrentDirectory, "data", "tools"),
            ToolHub,
            new HttpToolInvoker(Logger),
            Logger);
        foreach (var change in ToolManifests.LoadAll())
            Logger.Info($"[ToolHub] 清单已加载：{change}");
        ToolManifests.StartWatching();

        // V3.3：微信窗口只读工具（UIAutomation，由 App 注入实现）
        if (wechat is not null)
        {
            ToolDefinition SimpleTool(string name, string desc, Func<CancellationToken, Task<string>> body) => new()
            {
                Name = name,
                Description = desc,
                Category = ToolCategory.Context,
                Parameters = [],
                ExecuteAsync = async (_, toolCt) =>
                {
                    try { return new ToolCallResult { Success = true, Output = await body(toolCt) }; }
                    catch (Exception ex) { return new ToolCallResult { Success = false, Error = ex.Message }; }
                },
            };

            Tools.Register(SimpleTool("wechat_find_window", "列出微信桌面窗口（只读）。",
                async toolCt => string.Join("\n", (await wechat.ListWeChatWindowsAsync(toolCt)).Select(w => w.ToString()))));
            Tools.Register(SimpleTool("read_visible_chat", "读取微信窗口当前可见聊天文本（只读）。",
                async toolCt =>
                {
                    var r = await wechat.ReadVisibleChatAsync(toolCt);
                    return r.Success ? r.Text : (r.Error ?? "读取失败");
                }));
        }

        // 第三阶段补充：把"唤醒 / 回正微信窗口"做成 Agent 可调用的能力（§2 的同类需求）
        // 实测"窗口卡住"人手只要在空白处点一下就恢复；有了这个工具，Agent 也能自己先唤醒再重试。
        if (wechatAction is not null)
        {
            var action = wechatAction;
            Tools.Register(new ToolDefinition
            {
                Name = "wechat_wake_window",
                Description = "唤醒并回正微信窗口：最小化/托盘会先还原，失焦会重新抢回键盘焦点，"
                            + "卡住的浮层/下拉会在窗口空白处点一下收掉，最后等到窗口位置稳定。"
                            + "**当微信相关的读写操作报『未就绪 / 失焦 / 窗口没找到 / 输入通道卡住』时，"
                            + "先调这个工具，然后重试原来那一步**——不要直接告诉用户做不到。",
                Category = ToolCategory.Context,
                Parameters = [],
                ReadOnly = true,
                ExecuteAsync = async (_, ct) =>
                {
                    var r = await action.WakeWindowAsync(ct);
                    return new ToolCallResult
                    {
                        Success = r.Success,
                        Output = r.Success ? r.Detail : "",
                        Error = r.Error,
                    };
                },
            });
        }

        // V4.1 主题扫描工具（scan_topic）——让 agent 在对话里就能用"模型扩词 + 优先队列 + 热点深扫"
        // 的同一套逻辑，而不是只有任务后台用得上。
        if (TopicScan is not null)
        {
            var scanner = TopicScan;
            Tools.Register(new ToolDefinition
            {
                Name = "scan_topic",
                Description = "按主题扫微信消息：模型先把主题扩成聊天里真能搜到的相关词，跨会话检索后按命中密度排优先队列"
                            + "（命中多的会话多扫/深读，少的少扫），返回命中原文与队列。适合「最近有人提过某个话题吗」。",
                Category = ToolCategory.Search,
                Parameters =
                [
                    new ToolParameterSpec { Name = "topic", Type = ToolParamType.String, Description = "主题词，如 就业信息 / 租房 / 体检", Required = true },
                    new ToolParameterSpec { Name = "lookback_days", Type = ToolParamType.Integer, Description = "回溯天数（默认 7）", Default = 7, Minimum = 1, Maximum = 90 },
                ],
                ExecuteAsync = async (argsJson, ct) =>
                {
                    var started = DateTime.UtcNow;
                    try
                    {
                        var (topic, days) = ParseScanArgs(argsJson);
                        if (topic.Length == 0)
                            return new ToolCallResult { Success = false, Error = "缺少 topic 参数" };

                        var since = DateTimeOffset.Now.AddDays(-days).ToUnixTimeSeconds();
                        var outcome = await scanner.ScanAsync(topic, profileJson: null, since, ct);
                        return new ToolCallResult
                        {
                            Success = true,
                            Output = outcome.Hits.Count == 0 ? outcome.Note : outcome.Note + "\n" + outcome.Context,
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

        Workflow = new WorkflowEngine(
            Chat,
            HybridRetriever,
            PreciseReadAsync,
            Tools,
            Settings.Agent,
            Logger,
            ScanCommitmentsAsync);

        // 2.0 Agent 装配（P20）：Planner(规则默认) + SkillRegistry + BridgeMemoryBackend 真实底座
        var memoryBackend = new BridgeMemoryBackend(Bridge, HybridRetriever, Settings.Agent, Logger);
        MemoryBackend = memoryBackend;
        _planner = new Planner(SkillCatalog.Default(), Settings.Agent, Chat, Logger);

        // V3.4：写操作 Skill（Action 类）——人工确认闸门默认拒绝，未注入 UI 时不会误发消息
        // 第三阶段：闸门不再自带弹窗，而是接到"唯一交互口"上（§4 只有一套确认机制）；
        // 未注入 UI 时交互口是 DenyAll → 写操作一律被拒，装错也不会误发消息。
        UserInteraction = userInteraction ?? new Core.Agent.Interaction.DenyAllUserInteraction();

        // 第三阶段补充（授权机制）：写操作授权（"以后都允许给某某发消息"）落盘保存；
        // 闸门与 action Skill 共用**同一份**，否则会出现"闸门放行了、Skill 台账却写着'用户确认'"
        // 这种对不上的情况。
        ActionGrants = new Agent.FileActionGrantStore(logger: Logger);

        // 写操作链路的留痕出口：GUI 用 ActionLog（会落文件），无界面场景退回普通日志。
        // 为什么必须能落文件：这条链路最容易出现"看着像没反应"的静默跳步（解析不出、被白名单拒、
        // 核对超时…），只写控制台等于没有证据——实测排查时只能靠"日志里一条都没有"反推。
        var writeAudit = actionAudit ?? (line => Logger.Info(line));

        // 对象核对（强规则）：写操作**动手前必须先查本地会话目录**。
        // 以前这条只在 tools Skill 的提示词里"请模型先 find_sessions"——可规则路径（action Skill）
        // 根本不经过那儿，于是它既判不出私聊/群聊，又会把"名字没打进搜索框"说成"没有这个人"。
        // 现在把这件事做成代码里的硬步骤：Skill 与工具两条入口共用同一个核对器。
        var targetResolver = new Core.Agent.Action.WeChatTargetResolver(
            new Core.Agent.Action.MemoryBackendSessionDirectory(memoryBackend),
            writeAudit);

        // 发送对象白名单（第三阶段补充）：名单非空时，名单外的对象**连确认弹窗都不弹，直接拒绝**。
        // 配置在 appsettings.json 的 agent.sendAllowList；空数组 = 不启用（行为与以前一致）。
        var sendAllowList = new Core.Agent.Action.SendAllowList(Settings.Agent.SendAllowList);
        if (sendAllowList.Enabled)
            Logger.Info($"[Action] 发送白名单已启用：{sendAllowList.Describe()}（名单外一律拒绝发送）");

        var confirmation = actionConfirmation ?? new Core.Agent.Interaction.ActionConfirmationGate(
            UserInteraction, ActionGrants, audit: writeAudit);
        var actionSkill = new ActionSkill(
            wechatAction,
            confirmation,
            audit: writeAudit,
            grants: ActionGrants,
            targets: targetResolver,
            sendAllowList: sendAllowList);

        // 第三阶段补充：把"请求用户决策"做成 Agent 可调用的一等能力（§2/§3）
        new Agent.UserInteractionToolProvider(UserInteraction).RegisterAll(Tools);

        // 第三阶段：环境感知层（§15）——Agent 的环境工具与 GUI 的「环境」页共用同一份快照
        Environment = new Environment.EnvironmentService(this, uiContext);
        new Environment.EnvironmentToolProvider(Environment).RegisterAll(Tools);

        // 第三阶段：任务自主管理（§19）——创建/查询/修改/启停/立即执行/删除，写操作走人工确认闸门
        new Missions.MissionToolProvider(
            MissionCatalog,
            Scheduler,
            confirmation,
            PersistMissions,
            audit: line => Logger.Info(line),
            // 执行记录（长期记忆）：Agent 也能查"那个任务跑过几次/上次跑出什么"
            runs: MissionRuns).RegisterAll(Tools);

        // V3.5b：通用工具 Skill——每次执行现读注册表，热注册的新工具下一轮即可被 LLM 选用
        var toolSkill = new ToolCallingSkill(Chat, Tools, Settings.Agent, Logger);

        // V4.0：任务编排 Skill——把"追踪某人/每天汇总"这类需求变成任务草案（确认后才创建）
        var missionSkill = new MissionSkill();

        // V3.4：把写操作同时暴露为 Agent 可调用的工具（真实 JSON Schema + 同一套确认闸门）
        if (wechatAction is not null)
            new WeChatActionToolProvider(wechatAction, confirmation, Logger, targetResolver, sendAllowList).RegisterAll(Tools);

        _skillRegistry = SkillRegistry.BuildDefault(memoryBackend, wechat, extra: [actionSkill, toolSkill, missionSkill]);

        // 作答器：证据已由 Agent 找齐，这里只负责"讲成人话"（关掉则退化为原始记录摘录）
        _answerComposer = Settings.Agent.EnableAnswerSynthesis
            ? new Core.Agent.Answer.LlmAnswerComposer(Chat, Logger)
            : null;

        Agent = new ConversationalAgent(
            _planner,
            _skillRegistry,
            Settings.Agent,
            Logger,
            // 让模型自己分析意图、决定"该做什么"（可配置；失败自动回退规则规划）
            enableLlmPlanning: Settings.Agent.EnableLlmPlanning,
            composer: _answerComposer);
    }

    /// <summary>
    /// 后台常驻补齐索引（UI 启动时调用一次）。
    /// 索引只覆盖"建索引那一刻"的数据：不常补的话，"翻出我最近的聊天"永远差几天
    /// （实测索引停在 9.8、用户已经聊到 9.11）。这里按 Interval 周期增量补齐，
    /// 上一轮没跑完就跳过这一轮；失败只记日志，不影响主流程。
    /// </summary>
    public void StartBackgroundIndexRefresh()
    {
        // 可重复调用（V4.1）：设置页改完"索引补齐间隔"要能立即生效，否则新间隔要等下次启动。
        _indexTimer?.Dispose();
        _indexTimer = null;

        var minutes = Settings.Agent.IndexRefreshMinutes;
        if (minutes <= 0)
        {
            Logger.Info("[Index] 后台索引补齐已关闭（Agent.IndexRefreshMinutes=0）。");
            return;
        }

        // 首次**延迟**再跑：Bridge 是单线程的，补齐索引（实测一次可跑 30~40s）会占满队列，
        // 把启动后第一波页面请求（rag_timeline / rag_commitments / rag_profiles）挤到超时，
        // 用户看到的就是"一直加载中/加载失败"。先让首屏的请求过去，再补索引。
        var firstDelay = TimeSpan.FromSeconds(30);
        _indexTimer = new Timer(_ => _ = RefreshIndexOnceAsync(), null,
            firstDelay, TimeSpan.FromMinutes(minutes));
        Logger.Info($"[Index] 后台索引补齐已启动：{firstDelay.TotalSeconds:0} 秒后首次补，之后每 {minutes} 分钟一次。");
    }

    private Timer? _indexTimer;
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    private async Task RefreshIndexOnceAsync()
    {
        if (!await _indexGate.WaitAsync(0)) return;   // 上一轮还在跑 → 跳过本轮
        try
        {
            var resp = await Bridge.RequestAsync("rag_incremental_index",
                new Dictionary<string, object?> { ["hard_cap"] = 200, ["embed_batch"] = 32 },
                timeoutSeconds: 900);
            if (resp.Success && resp.Data is System.Text.Json.JsonElement je &&
                je.TryGetProperty("ok", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                var added = je.TryGetProperty("added_chunks", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number ? a.GetInt32() : 0;
                var msgs = je.TryGetProperty("new_messages", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.Number ? m.GetInt32() : 0;
                if (added > 0 || msgs > 0)
                    Logger.Info($"[Index] 已补齐新聊天：+{added} 片段 / {msgs} 条消息。");
            }
            else
            {
                Logger.Warn($"[Index] 增量索引未完成：{resp.Error ?? "未知原因"}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Index] 增量索引失败：{ex.Message}");
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private Planner? _planner;
    private SkillRegistry? _skillRegistry;
    private Core.Agent.Answer.IAnswerComposer? _answerComposer;

    /// <summary>
    /// 造一个"跑任务用"的子智能体（每个任务独立实例，上下文互不串扰）。
    /// **能力必须与主智能体完全一致**：同一份 Planner、同一份 Skill 注册表
    /// （含 tools / action）、同一个作答器。
    ///
    /// 实测教训：这里原来硬编码 <c>enableLlmPlanning: false</c>，等于把任务里的子智能体降级成
    /// "只按规则挑能力"——模型压根没有机会判断该调哪个工具，规则覆盖不到的任务就只是空转一圈，
    /// 用户看到的就是"跑在任务里的子 agent 完全不会操作"。规划开关改为跟随配置（默认开），
    /// 与对话页同一口径。
    /// </summary>
    public ConversationalAgent CreateAgent()
        => new(
            _planner ?? throw new InvalidOperationException("Agent 组件未初始化"),
            _skillRegistry ?? throw new InvalidOperationException("Agent 组件未初始化"),
            Settings.Agent,
            Logger,
            enableLlmPlanning: Settings.Agent.EnableLlmPlanning,
            composer: _answerComposer);

    /// <summary>Workflow 承诺扫描委托：调 rag_commitments（索引行级扫描）。</summary>
    private async Task<IReadOnlyList<CommitmentCandidate>> ScanCommitmentsAsync()
    {
        var resp = await Bridge.RequestAsync("rag_commitments",
            new Dictionary<string, object?> { ["limit"] = 200 }, timeoutSeconds: 60);
        if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je ||
            !je.TryGetProperty("candidates", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];
        return CommitmentDataParser.Parse(arr);
    }

    /// <summary>解析 scan_topic 工具参数（缺省回溯 7 天）。</summary>
    private static (string Topic, int Days) ParseScanArgs(string argsJson)
    {
        var topic = "";
        var days = 7;
        if (string.IsNullOrWhiteSpace(argsJson)) return (topic, days);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("topic", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
                topic = (t.GetString() ?? "").Trim();
            if (doc.RootElement.TryGetProperty("lookback_days", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Number)
                days = Math.Clamp(d.GetInt32(), 1, 90);
        }
        catch (System.Text.Json.JsonException) { /* 参数坏掉就按缺省来 */ }
        return (topic, days);
    }

    /// <summary>Workflow 精读委托：读指定会话时间段的原文。</summary>
    private async Task<IReadOnlyList<ToolMessageInput>> PreciseReadAsync(
        string sessionId, long begin, long end, int limit)
    {
        var resp = await Bridge.RequestAsync("read_messages", new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["limit"] = limit,
            ["begin"] = begin,
            ["end"] = end,
        }, timeoutSeconds: 30);
        if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je ||
            je.ValueKind != System.Text.Json.JsonValueKind.Array) return [];

        var outList = new List<ToolMessageInput>();
        foreach (var m in je.EnumerateArray())
        {
            outList.Add(new ToolMessageInput(
                sessionId,
                m.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "",
                m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                m.TryGetProperty("create_time", out var t) ? t.GetInt64() : 0L));
        }
        return outList;
    }

    public async Task ShutdownAsync()
    {
        ToolManifests?.Dispose();
        try
        {
            await Bridge.RequestAsync("/exit", timeoutSeconds: 5);
        }
        catch
        {
            // ignore
        }
        Bridge.Dispose();
    }

    public void Dispose()
    {
        _indexTimer?.Dispose();
        _indexTimer = null;
        Bridge.Dispose();
    }
}
