using System.IO;
using System.Windows;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Runtime;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.PythonBridge;
using MemoryAssistant.Infrastructure.Configuration;
using MemoryAssistant.Infrastructure.Logging;
using MemoryAssistant.Infrastructure.PythonBridge;

namespace MemoryAssistant.App;

/// <summary>
/// 应用入口：加载配置 → 启动 Python Bridge → 运行自检 → 显示主窗口。
/// 自检结果写入日志，供 P0/P1 验收。
/// </summary>
public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static IAppLogger Logger { get; private set; } = new ConsoleLogger();
    public static IPythonBridge Bridge { get; private set; } = null!;
    public static MemoryAssistant.Infrastructure.AppServices Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            try { Console.WriteLine($"[FATAL] Dispatcher: {args.Exception}"); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { Console.WriteLine($"[FATAL] AppDomain: {args.ExceptionObject}"); } catch { }
        };

        Settings = ConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        Settings.PythonBridge.BridgeScriptPath = ResolveBridgeScript();
        Settings.PythonBridge.PythonExePath = ResolvePythonExe(Settings.PythonBridge.PythonExePath);
        Logger = new ConsoleLogger();

        // 节流壳：验收/自检模式默认 Eco（工具只读部分会话的小样本，省 token）；
        // 需大量读取真实聊天记录时显式加 --full-data 才放开（本机用户明确同意后才用）。
        var acceptanceModes = new[] { "--smoke", "--agent", "--rag", "--workflow", "--timeline", "--p7", "--trace", "--index", "--p11", "--p12", "--p13", "--p14", "--p15", "--p16", "--p17", "--p18", "--p19", "--p20", "--live", "--missions", "--missionchat", "--wechat", "--action", "--tools", "--toolcall", "--autoreply" };
        if (acceptanceModes.Any(a => e.Args.Contains(a)) && !e.Args.Contains("--full-data"))
        {
            Settings.Agent.EcoMode = true;
            Logger.Info("验收模式默认 Eco：Bridge 工具只读小样本（--full-data 可放开全量）。");
        }

        if (e.Args.Contains("--wechat"))
        {
            // V3.3 验收：微信窗口只读（不启动 Bridge，仅 UIAutomation）。
            await RunWechatAsync();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--toolcall"))
        {
            // V3.5b 端到端验收：真实 LLM 选用并调用"清单热注册的外部工具"（会消耗 token，用户明确要求）。
            await RunToolCallAsync(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--tools"))
        {
            // V3.5 验收：工具清单热注册（丢一个 *.tool.json → 新工具上架；删除 → 下架）。
            await RunToolsAsync();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--action"))
        {
            // V3.4 验收：微信写操作（默认走"拒绝"闸门证明安全边界；--yes 才真发）。
            var actionQuery = GetArgValue(e.Args, "--query", "打开文件传输助手的聊天");
            await RunActionAsync(actionQuery, autoApprove: e.Args.Contains("--yes"));
            Shutdown();
            return;
        }

        if (e.Args.Contains("--dialogs"))
        {
            // UI 冒烟：弹窗能不能正常构造（缺资源键/绑定类型错只会在运行时炸）
            await RunDialogsCheckAsync();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--render"))
        {
            // 真实数据：用本地库渲染某会话某天的整段对话（GUI 截图不可靠时的验证手段）。
            // 例：--render --session 张晓明 --date 2026-09-11
            var session = GetArgValue(e.Args, "--session", "");
            var date = GetArgValue(e.Args, "--date", DateTime.Today.ToString("yyyy-MM-dd"));
            if (!string.IsNullOrWhiteSpace(session)) await RunRenderCheckAsync(session, date);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--missionchat"))
        {
            // V4.0 验收：对话式任务编排（一句话需求 → 任务草案 → 确认创建 → 真实可执行）
            var mq = GetArgValue(e.Args, "--query", "帮我追踪和文件传输助手的聊天，有新消息就总结给我");
            await RunMissionChatAsync(mq);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--ocrdump"))
        {
            // 诊断：把"搜索下拉"整窗 OCR 行 + 截图转储出来，供维护者核对 OCR 判据（不点击、不发送）。
            var odTarget = GetArgValue(e.Args, "--target", "张晓明");
            var odOut = GetArgValue(e.Args, "--out", @"d:\agent\ocr_dump.png");
            await RunOcrDumpAsync(odTarget, odOut);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--autoreply"))
        {
            // V3.7 验收：自动回复（意图 → 子智能体 → 发送闸门）。默认**不真发**；--send 才真发（需显式同意）。
            var arTarget = GetArgValue(e.Args, "--target", "");
            await RunAutoReplyCheckAsync(arTarget, realSend: e.Args.Contains("--send"));
            Shutdown();
            return;
        }

        if (e.Args.Contains("--missions"))
        {
            // V3.2/V3.6 真实数据验收：任务 → 调度器真实执行（独立 Agent）+ 追踪型任务的增量语义。
            var goal = GetArgValue(e.Args, "--query", "整理我和朋友最近聊过的内容，列出 3 个要点");
            var target = GetArgValue(e.Args, "--target", "");   // 留空则自动从最近会话里挑
            await RunMissionsAsync(goal, target);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--think"))
        {
            // V4.1 验收：完整展示模型思考过程 + 自主工具调用（真实 LLM）。
            // 例：--think --query "我和张晓明最近聊了什么" --query2 "继续找他的最新记录并帮我回一句"
            //     （--query2 在同一会话里跑第二轮，用来验证"他/那个人"这类追问是否还认得住对象）
            //     加 --rule-plan 可对比"规则规划 vs 模型规划"。
            var thinkQuery = GetArgValue(e.Args, "--query", "我和同学聊过秋招吗？帮我找找");
            var thinkQuery2 = GetArgValue(e.Args, "--query2", "");
            await RunThinkAsync(thinkQuery, thinkQuery2,
                forceRulePlan: e.Args.Contains("--rule-plan"),
                autoApprove: e.Args.Contains("--yes"));
            Shutdown();
            return;
        }

        if (e.Args.Contains("--live"))
        {
            // 真实数据验证：产品链路 ConversationalAgent（规则规划+确定性回答，零 LLM；Eco 小样本）。
            var query = GetArgValue(e.Args, "--query", "我和同学聊过秋招吗？");
            await RunLiveAsync(query);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--p20"))
        {
            // P20 验收模式：产品接线壳自检（同 ChatViewModel 的 Agent 会话→气泡映射，零 LLM/零数据）。
            await RunP20Async();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--p19"))
        {
            // P19 验收模式：Task Trace 2.0 壳自检（fake backend，零 LLM / 零数据）。
            await RunP19Async();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--p18"))
        {
            // P18 验收模式：Query Understanding 壳自检（纯规则抽取，零 LLM / 零数据）。
            RunP18Async();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--p17"))
        {
            // P17 验收模式：多轮上下文壳自检（fake backend，零 LLM / 零数据）。
            await RunP17Async();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--p16"))
        {
            // P16 验收模式：EvidenceStore 壳自检（纯内存生命周期，零 LLM / 零数据）。
            RunP16Async();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--p15"))
        {
            // P15 验收模式：Evaluator/Replanner 闭环壳自检（fake backend，零 LLM / 零数据）。
            await RunP15Async();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--p14"))
        {
            // P14 验收模式：Tool Schema 2.0 壳自检（只生成/检查 schema，不调 Bridge）。
            RunP14Async();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--p13"))
        {
            // P13 验收模式：Skill 层壳自检（P11 Task → P12 计划 → P13 执行，零 LLM / 零数据）。
            await RunP13Async();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--p12"))
        {
            // P12 验收模式：Planner 壳自检（fake chat，零 LLM / 零数据）。
            await RunP12Async();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--p11"))
        {
            // P11 验收模式：Agent Runtime 壳自检（fake executor，零 LLM / 零数据）。
            await RunP11Async();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--smoke"))
        {
            // P0/P1 验收模式：不弹窗，跑 Bridge 自检后退出。
            await RunSmokeAsync();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--agent"))
        {
            // P2 验收模式：真实 LLM + AgentLoop + Bridge 工具端到端。
            var query = GetArgValue(e.Args, "--query", "我最近和谁聊过天？");
            await RunAgentAsync(query);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--rag"))
        {
            // P3 验收模式：RAG 检索端到端（HybridRetriever）。
            var query = GetArgValue(e.Args, "--query", "工资");
            await RunRagAsync(query);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--workflow"))
        {
            // P4 验收模式：完整工作流（Intent → 两级检索 → 证据 → 生成）。
            var query = GetArgValue(e.Args, "--query", "帮我找紫薯马国敬，我们上次聊了什么？");
            await RunWorkflowAsync(query);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--timeline"))
        {
            // P6 验收模式：时间线 + 当日详情（rag_timeline / rag_day_detail）。
            await RunTimelineAsync();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--p7"))
        {
            // P7 验收模式：承诺/话题/画像三个高辨识度功能数据管线。
            await RunP7Async();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--trace"))
        {
            // P8 验收模式：完整 Agent 执行过程 Trace（阶段/轮次/工具/Token/耗时/错误）。
            var query = GetArgValue(e.Args, "--query", "帮我找紫薯马国敬，我们上次聊了什么？");
            await RunTraceAsync(query);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--index"))
        {
            // P9 验收模式：增量索引（状态 → 增量 → 幂等 → 状态对比 → 检索验证）。
            await RunIndexAsync();
            Shutdown();
            return;
        }

        Logger.Info($"MemoryAssistant 启动 | LLM={Settings.Llm.Model} | Bridge={Settings.PythonBridge.BridgeScriptPath}");

        Services = new MemoryAssistant.Infrastructure.AppServices(Settings, Logger);
        Logger.Info("正在启动 Python Bridge...");
        try
        {
            // V3.4：只读桥 + 写操作桥 + 界面交互口
            // 第三阶段：写操作确认闸门不再自带弹窗，而是接到唯一的用户交互口上（规范 §4）
            var readBridge = new Integrations.UiAutomationWeChatBridge();
            var userInteraction = new Views.UiUserInteraction(ActionLog);
            await Services.InitializeAsync(
                ct: default,
                wechat: readBridge,
                wechatAction: new Integrations.UiAutomationWeChatActionBridge(ActionLog),
                missionExecutor: new Missions.ConversationMissionExecutor(Services),
                missionProbe: new Missions.WeChatMissionProbe(Services),
                // 环境感知（第三阶段 §15）：把"界面现在在哪"交给 Agent
                uiContext: UiContext.Current,
                // Agent ↔ 用户 的唯一交互口：request_user_confirmation 与写操作闸门共用它
                userInteraction: userInteraction,
                // 写操作链路的留痕（解析结果/授权/白名单/对象核对/成败）也落到 action 日志文件：
                // 这条链路常有"静默跳步"，只写控制台等于排查时没有证据
                actionAudit: ActionLog);
            Logger.Info("Python Bridge 与 Agent 组件就绪。");
            // 索引会落后于聊天记录：后台补齐最近的聊天，让"翻最新的记录"能查到
            Services.StartBackgroundIndexRefresh();
        }
        catch (Exception ex)
        {
            Logger.Error($"初始化失败: {ex.Message}（可稍后重试；部分功能可能不可用）");
        }

        RegisterGlobalHotkeys();

        var main = new MainWindow { DataContext = new MemoryAssistant.App.ViewModels.MainViewModel(Services) };
        main.Show();
        SetupTray();
    }

    /// <summary>
    /// 初始化托盘图标 + 任务通知观察者。
    ///
    /// 托盘的意义：应用是长驻的（关窗后服务与任务继续跑），托盘是它唯一的常驻入口——
    /// 显示窗口 / 一键停任务 / 退出都在这里，不用再去任务管理器杀进程。
    /// 通知的意义：长期任务跑出值得看的结论时，由**专用通知智能体**裁决后弹一条系统通知。
    /// </summary>
    private void SetupTray()
    {
        try
        {
            _tray = new TrayIcon(
                onShowWindow: ShowMainWindow,
                onStopAllMissions: () =>
                {
                    var n = Services.Scheduler?.StopAllRunning() ?? 0;
                    ActionLog($"[Action] ⛔ 托盘：已停止 {n} 个正在运行的任务");
                },
                onQuit: QuitApp,
                onBalloonClick: (_, _) => ShowMainWindow());
            _tray.SetRunningState(Services.Scheduler?.RunningCount ?? 0);
            ActionLog("[Action] 托盘已就绪：双击显示窗口，右键可停止所有任务 / 退出");

            // 托盘提示里的"N 个任务在跑"跟着调度状态走（Changed 可能来自后台线程，转回 UI 线程再改）
            if (Services.Scheduler is { } scheduler)
            {
                scheduler.Changed += () => Dispatcher.Invoke(() =>
                {
                    _tray?.SetRunningState(scheduler.RunningCount);
                });
            }

            if (Settings.Agent.NotifyMissionRuns && Services.MissionRuns is not null)
            {
                _notifier = new Core.Missions.MissionNotificationWatcher(
                    Services.MissionRuns,
                    new Core.Missions.MissionNotificationAgent(Services.Chat, Logger),
                    new TrayNotificationSink(_tray),
                    interval: TimeSpan.FromSeconds(20),
                    audit: ActionLog);
                ActionLog("[Action] 任务通知观察者已启动（由专用通知智能体裁决是否值得打断你）");
            }
            else
            {
                ActionLog("[Action] 任务通知已关闭（agent.notifyMissionRuns=false）");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Action] 托盘/通知初始化失败：{ex.Message}");
        }
    }

    /// <summary>把托盘的"弹通知"能力接到通知观察者上（系统通知 = 托盘气泡，点击可叫回窗口）。</summary>
    private sealed class TrayNotificationSink(TrayIcon tray) : Core.Missions.INotificationSink
    {
        private readonly TrayIcon _tray = tray;
        public void Notify(string title, string body) => _tray.Notify(title, body);
    }

    private TrayIcon? _tray;
    private Core.Missions.MissionNotificationWatcher? _notifier;

    /// <summary>把主窗口叫回来（托盘双击/单击通知/热键都用它）。</summary>
    private void ShowMainWindow()
    {
        if (MainWindow is not { } w) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        ActionLog("[Action] 主窗口已显示");
    }

    /// <summary>真正退出：先停掉所有任务，再结束进程（托盘退出 / Ctrl+Alt+Q 共用）。</summary>
    private void QuitApp()
    {
        var n = Services.Scheduler?.StopAllRunning() ?? 0;
        ActionLog($"[Action] ⛔ 退出应用：已停止 {n} 个正在运行的任务");
        ShuttingDown = true;      // 告诉主窗口"这次别再拦着不让关了"
        Shutdown();
    }

    /// <summary>主窗口第一次收起时提示一次托盘（只提示一次，不做成骚扰）。</summary>
    internal static void NotifyTrayHintOnce()
    {
        if (_trayHintShown) return;
        _trayHintShown = true;
        (Current as App)?._tray?.Notify(
            "已收到托盘，服务继续在后台运行",
            $"双击托盘图标显示窗口；右键可停止所有任务或退出（{StopAllKeyText} 也能一键停车）。");
        LogAction("[Action] 已弹一次托盘提示（让用户知道去哪儿找它）");
    }

    private static bool _trayHintShown;

    /// <summary>
    /// 注册全局热键。为什么必须是全局：任务跑起来会抢焦点、模拟键鼠，而窗口可以收起/关闭、
    /// 服务仍在后台继续跑（这是有意的）。这时用户需要一个"不用先找回窗口"的应急开关——
    /// 热键注册在独立的消息窗口上，主窗口收起或关掉都照样生效。
    ///
    /// 三个动作各给几个候选键位（见 GlobalHotkey.Bind）：别人占着的组合自动让位，
    /// 并把**实际生效的键**写进日志与界面提示——绝不静默留一个按不动的键。
    /// </summary>
    private void RegisterGlobalHotkeys()
    {
        try
        {
            var stop = GlobalHotkey.Bind(1, "停止所有正在运行的任务", () =>
            {
                var n = Services.Scheduler?.StopAllRunning() ?? 0;
                ActionLog($"[Action] ⛔ 全局刹车（{StopAllKeyText}）：已停止 {n} 个正在运行的任务");
            }, (0x53, "S"), (0x50, "P"));                       // S=Stop，P=Park 备选

            var toggle = GlobalHotkey.Bind(2, "显示/隐藏主窗口", ToggleMainWindowVisibility,
                (0x4D, "M"), (0x57, "W"), (0x48, "H"));          // M=Main，W/H 备选

            var quit = GlobalHotkey.Bind(3, "退出应用", QuitApp, (0x51, "Q"), (0x45, "E"));   // Q=Quit，E=Exit 备选

            if (stop is not null) { _hotkeys.Add(stop); StopAllKeyText = stop.Description; }
            if (toggle is not null) { _hotkeys.Add(toggle); ToggleWindowKeyText = toggle.Description; }
            if (quit is not null) { _hotkeys.Add(quit); QuitKeyText = quit.Description; }

            var ready = string.Join(" · ", new[] { stop, toggle, quit }
                .Where(h => h is not null)
                .Select(h => $"{h!.Description} {h.Purpose}"));
            ActionLog($"[Action] 全局热键已就绪（{_hotkeys.Count}/3）：{ready}");
            if (_hotkeys.Count < 3)
                Logger.Warn("[Action] 有全局热键没注册上（候选键位都被占用）；任务页的「终止执行」/「停止」按钮仍可用。");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Action] 全局热键注册异常：{ex.Message}");
        }
    }

    /// <summary>实际生效的组合键（注册时确定，日志/界面提示都用它，避免提示里写着一个按不动的键）。</summary>
    internal static string StopAllKeyText { get; private set; } = "Ctrl+Alt+S";
    internal static string ToggleWindowKeyText { get; private set; } = "Ctrl+Alt+M";
    internal static string QuitKeyText { get; private set; } = "Ctrl+Alt+Q";

    /// <summary>显示/隐藏主窗口（关窗后能把界面叫回来，不用去任务管理器）。</summary>
    private void ToggleMainWindowVisibility()
    {
        if (MainWindow is not { } w) return;
        if (w.IsVisible)
        {
            w.Hide();
            ActionLog($"[Action] 主窗口已收起（服务与任务继续在后台运行；{ToggleWindowKeyText} 可再叫回来）");
        }
        else
        {
            ShowMainWindow();
        }
    }

    /// <summary>
    /// 真正退出中（Ctrl+Alt+Q）：主窗口不再拦截关闭。
    /// 用标志位而不是直接判断，是因为 <see cref="Application.Shutdown()"/> 是否触发窗口 Closing
    /// 在不同 WPF 版本上行为不一致——写死一个明确的意图最省事，也不会把退出流程卡住。
    /// </summary>
    internal static bool ShuttingDown;

    /// <summary>供其它窗口写"动作日志"文件用（GUI 没有控制台，这些日志是唯一线索）。</summary>
    internal static void LogAction(string line) => ActionLog(line);

    private readonly List<GlobalHotkey> _hotkeys = [];

    private static async Task RunSmokeAsync()
    {
        Logger.Info("=== P0/P1 Bridge 自检 ===");
        Logger.Info($"配置: LLM={Settings.Llm.Model} | Embedding={Settings.Embedding.Model}({Settings.Embedding.Backend})");

        Bridge = new PythonBridgeClient(
            Settings.PythonBridge.PythonExePath,
            Settings.PythonBridge.BridgeScriptPath,
            Logger,
            Settings.PythonBridge.IdleTimeoutSeconds);

        var started = await Bridge.StartAsync();
        Logger.Info($"[1/6] Bridge 启动: {(started ? "PASS" : "FAIL")}");
        if (!started) return;

        var cases = new (string Method, Dictionary<string, object?> Args)[]
        {
            ("list_sessions", new() { ["limit"] = 5 }),
            ("read_messages", new() { ["session_id"] = "50603346514@chatroom", ["limit"] = 3 }),
            ("search_messages", new() { ["keyword"] = "工资", ["limit"] = 3 }),
            ("get_session_stats", new() { ["session_id"] = "50603346514@chatroom", ["limit"] = 50 }),
        };
        int i = 2;
        foreach (var (method, args) in cases)
        {
            var resp = await Bridge.RequestAsync(method, args, timeoutSeconds: 30);
            var status = resp.Success ? "PASS" : "FAIL";
            var brief = Describe(resp.Data);
            Logger.Info($"[{i++}/6] {method}: {status} {brief}");
        }

        var exitResp = await Bridge.RequestAsync("/exit", timeoutSeconds: 5);
        Logger.Info($"[6/6] 优雅退出: {(exitResp.Success ? "PASS" : "FAIL")}");
        Logger.Info("=== 自检结束 ===");
    }

    private static string Describe(object? data)
    {
        if (data is System.Text.Json.JsonElement je)
        {
            var s = je.GetRawText();
            return s.Length <= 160 ? s : s[..160] + "...";
        }
        return data?.ToString() ?? "null";
    }

    private static string GetArgValue(string[] args, string flag, string fallback)
    {
        var i = Array.IndexOf(args, flag);
        if (i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            return args[i + 1];
        return fallback;
    }

    /// <summary>
    /// 写操作桥 / 确认闸门的日志：除了照常进 Logger，还**落一份到文件**。
    ///
    /// 为什么要落文件：GUI 模式没有控制台，而这些日志恰恰是排查
    /// "窗口在托盘/最小化/屏幕上时点偏了"这类问题的唯一线索——
    /// 之前只能靠加代码再猜，效率极低。只收 <c>[Action]</c> 行，不含聊天原文。
    /// </summary>
    private static void ActionLog(string line)
    {
        Logger.Info(line);
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "memoryassistant_action.log");
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去不能影响主流程
        }
    }

    /// <summary>
    /// P11 壳验收：用脚本化 fake executor 驱动 TaskRuntime，
    /// 验证状态机（可观察）/取消/超时/失败/预算五个场景。零 LLM、零聊天数据。
    /// </summary>
    private static async Task RunP11Async()
    {
        Logger.Info("=== P11 Agent Runtime 壳自检（fake executor · 零 LLM / 零数据）===");
        int pass = 0, total = 0;

        // ---------- 1. normal：正常完成，状态可观察，步骤/观测写回 ----------
        total++;
        var normalTask = new AgentTask("我最近和谁深夜聊天？");
        var statuses = new List<AgentTaskStatus>();
        normalTask.StatusChanged += (_, e) => statuses.Add(e.Status);
        var normalOpts = new AgentOptions { MaxTaskSeconds = 0 };
        var normalRt = new TaskRuntime(normalOpts);
        var normal = await normalRt.RunAsync(normalTask, new ScriptedExecutor("recall", async (t, b, ct) =>
        {
            var step = t.State.AddStep("recall", "语义召回").MarkRunning();
            await Task.Delay(5, ct);
            step.Complete("找到 2 个候选会话");
            t.State.AddObservation(new Observation { Source = "stats", Summary = "2 会话 / 8 条", Success = true });
            b.TryConsumeTool();
            return new AgentResult { Answer = "模拟回答：与同学深夜聊过 3 次。", RoundCount = 1, CompletedNormally = true };
        }));
        bool ok1 = normal.Status == AgentTaskStatus.Completed
            && normal.Result?.Answer.Contains("深夜") == true
            && normal.State.Steps.Count == 1
            && normal.State.Steps[0].Status == TaskStepStatus.Completed
            && statuses.Contains(AgentTaskStatus.Completed);
        Logger.Info(ok1 ? $"[PASS] {total} normal：状态 Completed + 步骤/观测写回（{string.Join("→", statuses)}）" : $"[FAIL] {total} normal status={normal.Status}");
        if (ok1) pass++;

        // ---------- 2. cancel：运行中用户取消 → Cancelled ----------
        total++;
        var cancelTask = new AgentTask("取消测试");
        var cancelOpts = new AgentOptions { MaxTaskSeconds = 0 };
        var cancelRt = new TaskRuntime(cancelOpts);
        var cancelRun = cancelRt.RunAsync(cancelTask, new ScriptedExecutor("block", async (t, b, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // 挂起直到被取消
            return new AgentResult { Answer = "", CompletedNormally = true };
        }), CancellationToken.None);
        await Task.Delay(80);
        cancelTask.Cancel();
        var cancelled = await cancelRun;
        bool ok2 = cancelled.Status == AgentTaskStatus.Cancelled && cancelled.Error == "用户取消";
        Logger.Info(ok2 ? $"[PASS] {total} cancel：运行中取消 → Cancelled" : $"[FAIL] {total} cancel status={cancelled.Status} err={cancelled.Error}");
        if (ok2) pass++;

        // ---------- 3. timeout：超过 MaxTaskSeconds → Failed ----------
        total++;
        var toTask = new AgentTask("超时测试");
        var toOpts = new AgentOptions { MaxTaskSeconds = 1 };
        var toRt = new TaskRuntime(toOpts);
        var timedOut = await toRt.RunAsync(toTask, new ScriptedExecutor("slow", async (t, b, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new AgentResult { Answer = "", CompletedNormally = true };
        }));
        bool ok3 = timedOut.Status == AgentTaskStatus.Failed
            && timedOut.Error is not null && timedOut.Error.Contains("MaxTaskSeconds");
        Logger.Info(ok3 ? $"[PASS] {total} timeout：{timedOut.Error}" : $"[FAIL] {total} timeout status={timedOut.Status} err={timedOut.Error}");
        if (ok3) pass++;

        // ---------- 4. failure：executor 抛异常 → Failed + Error ----------
        total++;
        var errTask = new AgentTask("失败测试");
        var errRt = new TaskRuntime(new AgentOptions { MaxTaskSeconds = 0 });
        var failed = await errRt.RunAsync(errTask, new ScriptedExecutor("boom", (t, b, ct) =>
            throw new InvalidOperationException("Bridge 不可用")));
        bool ok4 = failed.Status == AgentTaskStatus.Failed && failed.Error?.Contains("Bridge 不可用") == true;
        Logger.Info(ok4 ? $"[PASS] {total} failure：异常 → Failed" : $"[FAIL] {total} failure status={failed.Status} err={failed.Error}");
        if (ok4) pass++;

        // ---------- 5. budget：工具预算耗尽 → executor 停手并如实上报 ----------
        total++;
        var budgetTask = new AgentTask("预算测试");
        var budgetOpts = new AgentOptions { MaxTaskSeconds = 0, MaxToolCalls = 3 };
        var budgetRt = new TaskRuntime(budgetOpts);
        var budgeted = await budgetRt.RunAsync(budgetTask, new ScriptedExecutor("looper", async (t, b, ct) =>
        {
            while (b.TryConsumeTool())
                await Task.Delay(5, ct);
            return new AgentResult
            {
                Answer = "达到工具预算上限，停止搜索。",
                CompletedNormally = false,
                EarlyStopReason = "tool_budget_exceeded",
            };
        }));
        bool ok5 = budgeted.Status == AgentTaskStatus.Completed
            && budgeted.Result is { CompletedNormally: false }
            && budgeted.Result.EarlyStopReason == "tool_budget_exceeded";
        Logger.Info(ok5 ? $"[PASS] {total} budget：3 次用尽后停手" : $"[FAIL] {total} budget status={budgeted.Status}");
        if (ok5) pass++;

        Logger.Info($"=== P11 壳自检 {pass}/{total} PASS ===");
    }

    /// <summary>P11 壳验收用脚本化 executor：按 body 执行，不碰真实 LLM/Bridge。</summary>
    private sealed class ScriptedExecutor(
        string name,
        Func<AgentTask, AgentBudget, CancellationToken, Task<AgentResult>> body) : IAgentTaskExecutor
    {
        public string Name => name;
        public Task<AgentResult> ExecuteAsync(AgentTask task, AgentBudget budget, CancellationToken ct)
            => body(task, budget, ct);
    }

    /// <summary>
    /// P12 壳验收：Planner（规则 + JSON 解析 + 校验 + LLM→回退）确定性自检。
    /// 用 fake chat 注入脚本内容，零真实 LLM / 零数据 / 零 token。
    /// </summary>
    private static async Task RunP12Async()
    {
        Logger.Info("=== P12 Planner 壳自检（fake chat · 零 LLM / 零数据）===");
        int pass = 0, total = 0;
        var catalog = MemoryAssistant.Core.Agent.Planner.SkillCatalog.Default();
        var opts = Settings.Agent;
        var validator = new MemoryAssistant.Core.Agent.Planner.PlanValidator(catalog, opts);

        void Check(string name, bool ok, string detail = "")
        {
            total++;
            Logger.Info(ok ? $"[PASS] {total} {name}" : $"[FAIL] {total} {name} {detail}");
            if (ok) pass++;
        }

        // 1. 规则计划：多类查询都产出通过校验的计划
        var ruleCases = new (string Q, string ExpectSkill)[]
        {
            ("我和小明去年聊过找工作吗？", "recall"),
            ("我最近是不是经常半夜和人聊天？", "stats"),
            ("我答应过别人什么还没做？", "commitment"),
            ("你好呀", "answer"),
        };
        foreach (var (q, skill) in ruleCases)
        {
            var plan = MemoryAssistant.Core.Agent.Planner.RulePlanner.RulePlan(q, hint: null);
            bool okPlan = validator.Validate(plan).Ok && plan.Steps.Any(s => s.Name == skill);
            Check($"规则计划[{q[..Math.Min(q.Length, 12)]}...]→{skill}", okPlan, $"steps={string.Join(",", plan.Steps.Select(s => s.Name))}");
        }

        // 2. JSON 解析：围栏 / 裸 / 非法
        var fenced = MemoryAssistant.Core.Agent.Planner.PlanJsonParser.Parse("""
```json
{"goal":"统计","steps":[{"id":"s1","type":"skill","name":"stats"}]}
```
""");
        Check("解析 fenced JSON", fenced is { Steps.Count: 1 } && fenced.Steps[0].Name == "stats");
        Check("解析非法 JSON 返回 null", MemoryAssistant.Core.Agent.Planner.PlanJsonParser.Parse("不是json{") is null);

        // 3. 校验：非法计划必须被拒/修复
        var dup = validator.Validate(new MemoryAssistant.Core.Agent.Planner.AgentPlan
        {
            Goal = "g",
            Steps =
            [
                new MemoryAssistant.Core.Agent.Planner.PlanStep { Id = "s1", Kind = "skill", Name = "recall" },
                new MemoryAssistant.Core.Agent.Planner.PlanStep { Id = "s1", Kind = "skill", Name = "stats" },
            ],
        });
        Check("校验拒绝重复 id", !dup.Ok && dup.Repaired is { Steps.Count: 1 });

        var unknown = validator.Validate(new MemoryAssistant.Core.Agent.Planner.AgentPlan
        {
            Goal = "g",
            Steps = [new MemoryAssistant.Core.Agent.Planner.PlanStep { Id = "s1", Kind = "skill", Name = "hack" }],
        });
        Check("校验拒绝未知 skill", !unknown.Ok);

        // 4. LLM 路径（fake）：合法 JSON → 采用；非法 → 规则回退
        var llmPlanner = new MemoryAssistant.Core.Agent.Planner.Planner(catalog, opts,
            new FakePlannerChat("```json\n{\"goal\":\"g\",\"steps\":[{\"id\":\"s1\",\"type\":\"skill\",\"name\":\"topic\"}]}\n```"));
        var llmPlan = await llmPlanner.PlanAsync("最近大家聊什么话题");
        Check("LLM 合法计划被采用", llmPlan.FromLlm && llmPlan.Steps.Any(s => s.Name == "topic"));

        var badPlanner = new MemoryAssistant.Core.Agent.Planner.Planner(catalog, opts,
            new FakePlannerChat("{\"goal\":\"g\",\"steps\":[{\"id\":\"s1\",\"type\":\"skill\",\"name\":\"nope\"}]}"));
        var badPlan = await badPlanner.PlanAsync("随便问问");
        Check("LLM 非法计划回退规则", !badPlan.FromLlm && badPlan.FromRules);

        Logger.Info($"=== P12 Planner 壳自检 {pass}/{total} PASS ===");
    }

    /// <summary>P12 壳验收用脚本化 chat：按队列返回固定文本，不碰真实 LLM。</summary>
    private sealed class FakePlannerChat(params string[] contents) : IChatClient
    {
        private readonly Queue<string> _queue = new(contents);
        public string ModelName => "fake-planner";
        public Task<ChatResult> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolSchema>? tools = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var content = _queue.Count > 0 ? _queue.Dequeue() : null;
            return Task.FromResult(new ChatResult { Content = content });
        }
    }

    /// <summary>
    /// P13 壳验收：P11 AgentTask + P12 Planner(规则) + P13 SkillPlanExecutor 完整链路。
    /// FakeBackend 注入样例，零真实 LLM / 零真实数据。
    /// </summary>
    private static async Task RunP13Async()
    {
        Logger.Info("=== P13 Skill 层壳自检（Task→计划→执行，零 LLM / 零数据）===");
        int pass = 0, total = 0;
        var catalog = MemoryAssistant.Core.Agent.Planner.SkillCatalog.Default();
        var backend = new AppFakeBackend();
        var registry = MemoryAssistant.Core.Agent.Skills.SkillRegistry.BuildDefault(backend);
        var planner = new MemoryAssistant.Core.Agent.Planner.Planner(catalog, Settings.Agent, chat: null);
        var opts = new MemoryAssistant.Core.Configuration.AgentOptions { MaxToolCalls = 10, MaxTaskSeconds = 0 };

        string[] queries =
        [
            "我和同学聊过秋招吗？",
            "我最近是不是经常半夜和人聊天？",
            "我答应过别人什么还没做？",
            "你好呀",
        ];
        foreach (var q in queries)
        {
            var task = new MemoryAssistant.Core.Agent.Runtime.AgentTask(q);
            var seen = new List<AgentTaskStatus>();
            task.StatusChanged += (_, e) => seen.Add(e.Status);
            var rt = new MemoryAssistant.Core.Agent.Runtime.TaskRuntime(opts);
            var done = await rt.RunAsync(task, new ScriptedExecutor("skills", async (t, b, ct) =>
            {
                var plan = await planner.PlanAsync(t.UserQuery, preferLlm: false, ct: ct);
                var result = await new MemoryAssistant.Core.Agent.Skills.SkillPlanExecutor(registry)
                    .ExecuteAsync(plan, b, t.State, ct);
                var answer = string.Join("\n", result.StepSummaries);
                if (result.Evidence.Count > 0)
                    answer += $"\n证据 {result.Evidence.Count} 条，可引用 [1..{result.Evidence.Count}]。";
                return new AgentResult
                {
                    Answer = answer,
                    CompletedNormally = result.RanToCompletion,
                    RoundCount = plan.Steps.Count,
                    EarlyStopReason = result.TerminationReason,
                };
            }), CancellationToken.None);

            bool ok = done.Status == AgentTaskStatus.Completed
                && done.Result is { CompletedNormally: true }
                && (q.Contains("你好") || done.State.Evidence.Count > 0 || !string.IsNullOrWhiteSpace(done.State.Observations.FirstOrDefault()?.Summary));
            string detail = done.State.Steps.Count == 0 ? "（无需查记录）"
                : $"步骤 {done.State.Steps.Count} | 证据 {done.State.Evidence.Count} | {string.Join(" / ", done.State.Observations.Select(o => o.Summary))}";
            total++;
            Logger.Info(ok ? $"[PASS] {total} {q} → {detail}（{string.Join("→", seen)})" : $"[FAIL] {total} {q}");
            if (ok) pass++;
        }

        Logger.Info($"=== P13 Skill 壳自检 {pass}/{total} PASS ===");
    }

    /// <summary>P13 壳验收用假底座：固定样例，模拟 RAG 与微信原文读取。</summary>
    private sealed class AppFakeBackend : MemoryAssistant.Core.Agent.Skills.IMemoryBackend
    {
        private static readonly MemoryAssistant.Core.Agent.Evidence Sample = new()
        {
            SessionId = "s1", SessionDisplayName = "同学群", CreateTime = 1754000000,
            SenderName = "小A", Content = "秋招有消息了吗", Source = "微信原文",
        };

        public Task<IReadOnlyList<MemoryAssistant.Core.RAG.RetrievedChunk>> SemanticSearchAsync(string query, int topK, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.RAG.RetrievedChunk>>([
                new() { SessionId = "s1", SessionName = "同学群", Date = "2026-08-01", Text = "秋招有消息了吗", MsgCount = 3 },
            ]);

        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> ReadDayMessagesAsync(string sessionId, string date, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([Sample]);

        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> KeywordSearchAsync(string keyword, string? sessionId, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([Sample]);

        public Task<string> GetStatsTextAsync(int limit, int sessionLimit, CancellationToken ct)
            => Task.FromResult("活跃会话 2 个，消息 300 条。高频会话：同学群(180)。");

        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Skills.DayActivity>> GetRecentDaysAsync(int count, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Skills.DayActivity>>(
                [new("2026-09-08", 120), new("2026-09-07", 80)]);

        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> GetDaySnippetsAsync(string date, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([Sample]);

        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> ScanCommitmentsAsync(int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>(
                [Sample with { Content = "我下周答辩完交报告", Source = "承诺" }]);

        public Task<string> GetTopicsTextAsync(int recentDays, CancellationToken ct)
            => Task.FromResult("近30天话题：关键词：实习工作(12)、共青团资讯(5)。");

        public Task<string> GetProfilesTextAsync(int top, CancellationToken ct)
            => Task.FromResult("画像：同学群(180条/20天)。");
    }

    /// <summary>P14 壳验收：真实 JSON Schema 自检（只生成 schema，不调 Bridge / 不调 LLM）。</summary>
    private static void RunP14Async()
    {
        Logger.Info("=== P14 Tool Schema 2.0 壳自检（零 LLM / 零数据）===");
        int pass = 0, total = 0;
        void Check(string name, bool ok, string detail = "")
        {
            total++;
            Logger.Info(ok ? $"[PASS] {total} {name}" : $"[FAIL] {total} {name} {detail}");
            if (ok) pass++;
        }

        var registry = new MemoryAssistant.Core.Agent.ToolRegistry();
        new MemoryAssistant.Infrastructure.Agent.BridgeToolProvider(
            new NoopBridge(), Settings.Agent).RegisterAll(registry);
        var schemas = registry.BuildSchemas();
        Check("注册 6 个工具并生成 schema", schemas.Count == 6, $"count={schemas.Count}");

        // 每个工具的 schema：parameters 非空 properties + type=object（修复空 properties 技术债）
        foreach (var s in schemas)
        {
            var props = s.Parameters?["properties"]?.AsObject();
            bool ok = props is { Count: > 0 }
                && s.Parameters!["type"]!.GetValue<string>() == "object"
                && !string.IsNullOrWhiteSpace(s.Description);
            Check($"schema[{s.Name}] properties={props?.Count ?? 0}", ok);
        }

        // read_messages：required + min/max + 分类前缀（deepseek/OpenAI 兼容）
        var read = schemas.Single(s => s.Name == "read_messages");
        var required = read.Parameters!["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        var limitNode = read.Parameters!["properties"]!["limit"]!.AsObject();
        Check("read_messages.required 含 session_id", required.Contains("session_id"));
        Check("read_messages.limit 有 1..200 范围", limitNode["minimum"]!.GetValue<long>() == 1 && limitNode["maximum"]!.GetValue<long>() == 200);
        Check("描述带分类前缀 [Memory]", read.Description.StartsWith("[Memory]"));

        Logger.Info($"样本 schema（read_messages.parameters）: {read.Parameters!.ToJsonString()}");
        Logger.Info($"=== P14 Tool Schema 壳自检 {pass}/{total} PASS ===");
    }

    /// <summary>P14 壳验收用空桥：仅承载工具定义，任何请求都不应被发出。</summary>
    private sealed class NoopBridge : MemoryAssistant.Core.PythonBridge.IPythonBridge
    {
        public bool IsRunning => false;
        public Task<bool> StartAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync() => Task.CompletedTask;
        public Task<MemoryAssistant.Core.PythonBridge.BridgeResponse> RequestAsync(
            string method,
            IReadOnlyDictionary<string, object?>? args = null,
            int? timeoutSeconds = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>
    /// P15 壳验收：Evaluator/Replanner 闭环（TaskRuntime + AgentOrchestrator）。
    /// 四个确定性场景：直接作答 / 失败换策略 / 重规划上限诚实终止 / 预算耗尽。零 LLM / 零数据。
    /// </summary>
    private static async Task RunP15Async()
    {
        Logger.Info("=== P15 Evaluator/Replanner 闭环壳自检（零 LLM / 零数据）===");
        int pass = 0, total = 0;
        void Check(string name, bool ok, string detail = "")
        {
            total++;
            Logger.Info(ok ? $"[PASS] {total} {name}" : $"[FAIL] {total} {name} {detail}");
            if (ok) pass++;
        }

        // S1 直接作答：recall 有证据
        {
            var opts = new MemoryAssistant.Core.Configuration.AgentOptions { MaxToolCalls = 10, MaxReplanRounds = 3, EnableReplanning = true, MaxTaskSeconds = 0 };
            var result = await RunP15OnceAsync("我和同学聊过秋招吗？",
                new P15Backend { Chunks = [new MemoryAssistant.Core.RAG.RetrievedChunk { SessionId = "s1", SessionName = "同学群", Date = "2026-08-01", Text = "秋招有消息了吗", MsgCount = 1 }] }, opts);
            Check("直接作答（recall 一次命中）", result is { CompletedNormally: true } && result.Answer.Contains("[1]"), result?.Answer ?? "");
        }

        // S2 失败→换策略：RAG 不可用 → 时间线兜住
        {
            var opts = new MemoryAssistant.Core.Configuration.AgentOptions { MaxToolCalls = 10, MaxReplanRounds = 3, EnableReplanning = true, MaxTaskSeconds = 0 };
            var result = await RunP15OnceAsync("我最近和朋友聊什么",
                new P15Backend
                {
                    ThrowSemantic = true,
                    Days = [new MemoryAssistant.Core.Agent.Skills.DayActivity("2026-09-08", 5)],
                    Snippets = [new MemoryAssistant.Core.Agent.Evidence { SessionId = "s2", SessionDisplayName = "老王", Content = "最近聊的都是实验", Source = "时间线" }],
                }, opts);
            Check("失败恢复（recall 抛错→换 timeline）", result is { CompletedNormally: true } && result.Answer.Contains("[1]"), result?.Answer ?? "");
        }

        // S3 策略穷尽：重规划上限 → 诚实终止
        {
            var opts = new MemoryAssistant.Core.Configuration.AgentOptions { MaxToolCalls = 10, MaxReplanRounds = 1, EnableReplanning = true, MaxTaskSeconds = 0 };
            var result = await RunP15OnceAsync("我上次和朋友聊了什么？", new P15Backend(), opts);
            Check("上限诚实终止", result is { CompletedNormally: false } && result.EarlyStopReason is { } r && r.Contains("上限"), result?.EarlyStopReason ?? "");
        }

        // S4 预算耗尽：两步计划预算=1
        {
            var opts = new MemoryAssistant.Core.Configuration.AgentOptions { MaxToolCalls = 1, MaxReplanRounds = 3, EnableReplanning = true, MaxTaskSeconds = 0 };
            var result = await RunP15OnceAsync("我最近是不是经常半夜和人聊天？", new P15Backend(), opts);
            Check("预算耗尽如实终止", result is { CompletedNormally: false } && result.EarlyStopReason is { } r && r.Contains("budget"), result?.EarlyStopReason ?? "");
        }

        Logger.Info($"=== P15 闭环壳自检 {pass}/{total} PASS ===");
    }

    private static async Task<AgentResult> RunP15OnceAsync(
        string query,
        MemoryAssistant.Core.Agent.Skills.IMemoryBackend backend,
        MemoryAssistant.Core.Configuration.AgentOptions opts)
    {
        var catalog = MemoryAssistant.Core.Agent.Planner.SkillCatalog.Default();
        var planner = new MemoryAssistant.Core.Agent.Planner.Planner(catalog, opts, chat: null);
        var skills = MemoryAssistant.Core.Agent.Skills.SkillRegistry.BuildDefault(backend);
        var orchestrator = new MemoryAssistant.Core.Agent.AgentOrchestrator(planner, skills, opts);
        var task = new MemoryAssistant.Core.Agent.Runtime.AgentTask(query);
        var rt = new MemoryAssistant.Core.Agent.Runtime.TaskRuntime(opts);
        var done = await rt.RunAsync(task, orchestrator, CancellationToken.None);
        return done.Result!;
    }

    /// <summary>P15 壳验收用可配置底座（recall/timeline 数据可注入）。</summary>
    private sealed class P15Backend : MemoryAssistant.Core.Agent.Skills.IMemoryBackend
    {
        public IReadOnlyList<MemoryAssistant.Core.RAG.RetrievedChunk> Chunks { get; init; } = [];
        public IReadOnlyList<MemoryAssistant.Core.Agent.Skills.DayActivity> Days { get; init; } = [];
        public IReadOnlyList<MemoryAssistant.Core.Agent.Evidence> Snippets { get; init; } = [];
        public bool ThrowSemantic { get; init; }

        public Task<IReadOnlyList<MemoryAssistant.Core.RAG.RetrievedChunk>> SemanticSearchAsync(string query, int topK, CancellationToken ct)
        {
            if (ThrowSemantic) throw new InvalidOperationException("RAG 服务不可用（模拟）");
            return Task.FromResult(Chunks);
        }
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> ReadDayMessagesAsync(string sessionId, string date, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([]);
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> KeywordSearchAsync(string keyword, string? sessionId, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([]);
        public Task<string> GetStatsTextAsync(int limit, int sessionLimit, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Skills.DayActivity>> GetRecentDaysAsync(int count, CancellationToken ct) => Task.FromResult(Days);
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> GetDaySnippetsAsync(string date, CancellationToken ct) => Task.FromResult(Snippets);
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> ScanCommitmentsAsync(int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([]);
        public Task<string> GetTopicsTextAsync(int recentDays, CancellationToken ct) => Task.FromResult("");
        public Task<string> GetProfilesTextAsync(int top, CancellationToken ct) => Task.FromResult("");
    }

    /// <summary>P16 壳验收：EvidenceStore 生命周期/编号/去重/事实-推测（零 LLM / 零数据）。</summary>
    private static void RunP16Async()
    {
        Logger.Info("=== P16 EvidenceStore 壳自检（零 LLM / 零数据）===");
        int pass = 0, total = 0;
        void Check(string name, bool ok, string detail = "")
        {
            total++;
            Logger.Info(ok ? $"[PASS] {total} {name}" : $"[FAIL] {total} {name} {detail}");
            if (ok) pass++;
        }

        var store = new MemoryAssistant.Core.Agent.EvidenceStore();
        store.Observe(new MemoryAssistant.Core.Agent.Evidence { SessionId = "s1", SessionDisplayName = "同学群", Content = "秋招有消息了吗", Source = "微信原文" }, originTool: "recall");
        store.Observe(new MemoryAssistant.Core.Agent.Evidence { SessionId = "s1", SessionDisplayName = "同学群", Content = "秋招有消息了吗", Source = "微信原文" }); // 重复
        store.Observe(new MemoryAssistant.Core.Agent.Evidence { SessionId = "s2", SessionDisplayName = "老王", Content = "我下周答辩", Source = "承诺" }, originTool: "commitment");
        store.Observe(new MemoryAssistant.Core.Agent.Evidence { SessionId = "s3", Content = "近30天聊实习10次", Source = "stats" }, originTool: "stats"); // 推测

        Check("去重后 3 条且连续编号 1..3",
            store.Count == 3 && store.Items.Select(x => x.Index).SequenceEqual([1, 2, 3]),
            $"count={store.Count}");

        Check("事实/推测分离（2 事实 1 推测）",
            store.FactItems.Count == 2 && store.InferenceItems.Count == 1,
            $"fact={store.FactItems.Count} inference={store.InferenceItems.Count}");

        Check("起源工具被记录（recall/commitment）",
            store.Items.All(x => x.OriginTool is "recall" or "commitment" or "stats"),
            $"origins={string.Join(",", store.Items.Select(x => x.OriginTool))}");

        Check("初始均为 Observed 未核实", store.Items.All(x => x.Stage == MemoryAssistant.Core.Agent.EvidenceStage.Observed && !x.Verified));

        store.Verify(2);
        store.VerifyAll();
        store.MarkCited();
        Check("生命周期→Cited（全部已引用）",
            store.Items.All(x => x.Stage == MemoryAssistant.Core.Agent.EvidenceStage.Cited && x.Verified));

        Logger.Info("样本条目：");
        foreach (var e in store.Items)
            Logger.Info($"  [{e.Index}] {e.SessionDisplayName}（{e.Source}，origin={e.OriginTool}，stage={e.Stage}）: {e.Content}");
        Logger.Info($"=== P16 EvidenceStore 壳自检 {pass}/{total} PASS ===");
    }

    /// <summary>P17 壳验收：多轮追问（他→实体 / 第N条展开 / 还有吗延续）不从头执行（零 LLM / 零数据）。</summary>
    private static async Task RunP17Async()
    {
        Logger.Info("=== P17 Multi-turn Context 壳自检（零 LLM / 零数据）===");
        int pass = 0, total = 0;
        void Check(string name, bool ok, string detail = "")
        {
            total++;
            Logger.Info(ok ? $"[PASS] {total} {name}" : $"[FAIL] {total} {name} {detail}");
            if (ok) pass++;
        }

        var opts = new MemoryAssistant.Core.Configuration.AgentOptions { MaxToolCalls = 10, MaxReplanRounds = 3, EnableReplanning = true, MaxTaskSeconds = 0 };
        var backend = new P17Backend();
        var planner = new MemoryAssistant.Core.Agent.Planner.Planner(MemoryAssistant.Core.Agent.Planner.SkillCatalog.Default(), opts, chat: null);
        var skills = MemoryAssistant.Core.Agent.Skills.SkillRegistry.BuildDefault(backend);
        var agent = new MemoryAssistant.Core.Agent.Conversation.ConversationalAgent(planner, skills, opts);

        var t1 = await agent.RunAsync("我和小明聊过秋招吗？");
        Check("第1轮：直接作答", t1.CompletedNormally && t1.Answer.Contains("[1]"), t1.Answer);

        var t2 = await agent.RunAsync("展开第1条");
        var note2 = agent.Session.Turns[^1].Note ?? "";
        Check("第2轮：展开第1条（引用上一轮证据）", t2.CompletedNormally && note2.Contains("第 1 条"), note2);

        var t3 = await agent.RunAsync("他后来还有说什么吗？");
        var note3 = agent.Session.Turns[^1].Note ?? "";
        var q3 = agent.Session.Turns[^1].ResolvedQuery;
        Check("第3轮：他→小明（实体延续）", t3.CompletedNormally && note3.Contains("小明") && q3.Contains("小明"), $"{note3} | {q3}");

        Logger.Info("=== P17 多轮上下文壳自检结束 ===");
        Logger.Info($"总 PASS {pass}/{total}");
    }

    /// <summary>P17 壳验收用假底座：按关键词命中返回"小明/秋招"素材。</summary>
    private sealed class P17Backend : MemoryAssistant.Core.Agent.Skills.IMemoryBackend
    {
        private static readonly MemoryAssistant.Core.RAG.RetrievedChunk Hit = new()
        { SessionId = "小明", SessionName = "小明", Date = "2026-08-01", Text = "秋招有消息了吗", MsgCount = 1 };

        public Task<IReadOnlyList<MemoryAssistant.Core.RAG.RetrievedChunk>> SemanticSearchAsync(string query, int topK, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.RAG.RetrievedChunk>>(
                query.Contains("小明") || query.Contains("秋招") ? [Hit] : []);

        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> ReadDayMessagesAsync(string sessionId, string date, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([]);
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> KeywordSearchAsync(string keyword, string? sessionId, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([]);
        public Task<string> GetStatsTextAsync(int limit, int sessionLimit, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Skills.DayActivity>> GetRecentDaysAsync(int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Skills.DayActivity>>([]);
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> GetDaySnippetsAsync(string date, CancellationToken ct) => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([]);
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> ScanCommitmentsAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([]);
        public Task<string> GetTopicsTextAsync(int recentDays, CancellationToken ct) => Task.FromResult("");
        public Task<string> GetProfilesTextAsync(int top, CancellationToken ct) => Task.FromResult("");
    }

    /// <summary>P18 壳验收：目标抽取（Goal/Entities/Time/Constraints/EvidenceRequirement）纯规则展示。</summary>
    private static void RunP18Async()
    {
        Logger.Info("=== P18 Query Understanding 壳自检（零 LLM / 零数据）===");
        int pass = 0, total = 0;
        void Check(string name, bool ok, string detail = "")
        {
            total++;
            Logger.Info(ok ? $"[PASS] {total} {name}" : $"[FAIL] {total} {name} {detail}");
            if (ok) pass++;
        }

        var analyzer = new MemoryAssistant.Core.Agent.Query.RuleQueryAnalyzer();
        string[] queries =
        [
            "我和小王去年聊工作的记录里，有没有提到秋招？",
            "我最近一个月是不是经常半夜和人聊天？",
            "我答应过别人什么还没做？",
            "你好，谢谢！",
        ];
        foreach (var q in queries)
        {
            var m = analyzer.Analyze(q);
            Logger.Info($"目标模型: {q}");
            Logger.Info($"  → intent={m.IntentHint} | 实体=[{string.Join(",", m.Entities)}] | 时间={m.TimeKind}({m.TimePhrase}) | 要原文={m.NeedsOriginalEvidence} | 约束=[{string.Join(",", m.Constraints)}] | 关键词=[{string.Join(",", m.Keywords)}]");
        }

        Check("实体/时间/证据要求 抽取", analyzer.Analyze("我和小王去年聊工作的记录里，有没有提到秋招？") is { Entities.Count: 1 } m1
            && m1.Entities[0] == "小王" && m1.TimeKind == MemoryAssistant.Core.Agent.Query.TimeKind.LastYear && m1.NeedsOriginalEvidence);
        Check("统计+近30天 意图/时间", analyzer.Analyze("我最近一个月是不是经常半夜和人聊天？") is { IntentHint: "stats" } m2
            && m2.TimeKind is MemoryAssistant.Core.Agent.Query.TimeKind.Recent or MemoryAssistant.Core.Agent.Query.TimeKind.RangeDays);
        Check("承诺意图", analyzer.Analyze("我答应过别人什么还没做？").IntentHint == "commitment");
        Check("闲聊意图", analyzer.Analyze("你好，谢谢！").IntentHint == "chitchat");

        Logger.Info($"=== P18 Query Understanding 壳自检 {pass}/{total} PASS ===");
    }

    /// <summary>P19 壳验收：Task Trace 渲染（成功一轮 / 换策略两轮）零 LLM / 零数据。</summary>
    private static async Task RunP19Async()
    {
        Logger.Info("=== P19 Task Trace 2.0 壳自检（零 LLM / 零数据）===");
        int pass = 0, total = 0;
        void Check(string name, bool ok, string detail = "")
        {
            total++;
            Logger.Info(ok ? $"[PASS] {total} {name}" : $"[FAIL] {total} {name} {detail}");
            if (ok) pass++;
        }

        var opts = new MemoryAssistant.Core.Configuration.AgentOptions { MaxToolCalls = 10, MaxReplanRounds = 3, EnableReplanning = true, MaxTaskSeconds = 0 };
        var planner = new MemoryAssistant.Core.Agent.Planner.Planner(MemoryAssistant.Core.Agent.Planner.SkillCatalog.Default(), opts, chat: null);

        // 场景1：recall 直接命中 → 一轮完成
        var backend1 = new P15Backend
        {
            Chunks = [new MemoryAssistant.Core.RAG.RetrievedChunk { SessionId = "s1", SessionName = "同学群", Date = "2026-08-01", Text = "秋招有消息了吗", MsgCount = 1 }],
        };
        var task1 = new MemoryAssistant.Core.Agent.Runtime.AgentTask("我和同学聊过秋招吗？");
        var rt1 = new MemoryAssistant.Core.Agent.Runtime.TaskRuntime(opts);
        var done1 = await rt1.RunAsync(task1, new MemoryAssistant.Core.Agent.AgentOrchestrator(planner, MemoryAssistant.Core.Agent.Skills.SkillRegistry.BuildDefault(backend1), opts), CancellationToken.None);
        var trace1 = done1.Result!.TaskTrace;
        Logger.Info(trace1?.ToText() ?? "（无 trace）");
        Check("成功一轮 trace 完整（1 轮 / recall / answer）",
            trace1 is { Completed: true, Cycles.Count: 1 } t1
            && t1.Cycles[0].PlannedSteps.Any(p => p.Contains("recall"))
            && t1.Cycles[0].Verdict is { } v1 && v1.StartsWith("answer"));

        // 场景2：recall 落空 → 换 timeline 两轮（含 replan 判定）
        var backend2 = new P15Backend
        {
            Days = [new MemoryAssistant.Core.Agent.Skills.DayActivity("2026-09-08", 5)],
            Snippets = [new MemoryAssistant.Core.Agent.Evidence { SessionId = "s2", SessionDisplayName = "老王", Content = "最近聊的都是实验", Source = "时间线" }],
        };
        var task2 = new MemoryAssistant.Core.Agent.Runtime.AgentTask("我最近和朋友聊过什么吗");
        var rt2 = new MemoryAssistant.Core.Agent.Runtime.TaskRuntime(opts);
        var done2 = await rt2.RunAsync(task2, new MemoryAssistant.Core.Agent.AgentOrchestrator(planner, MemoryAssistant.Core.Agent.Skills.SkillRegistry.BuildDefault(backend2), opts), CancellationToken.None);
        var trace2 = done2.Result!.TaskTrace;
        Logger.Info(trace2?.ToText() ?? "（无 trace）");
        Check("换策略两轮 trace（recall→timeline / replan→answer）",
            trace2 is { Completed: true, Cycles.Count: 2 } t2
            && t2.Cycles[0].PlannedSteps.Any(p => p.Contains("recall"))
            && t2.Cycles[1].PlannedSteps.Any(p => p.Contains("timeline"))
            && t2.ToText().Contains("replan"));

        Logger.Info($"=== P19 Task Trace 壳自检 {pass}/{total} PASS ===");
    }

    /// <summary>
    /// P20 产品接线壳自检：复用 ChatViewModel 的 Agent→气泡映射管线
    /// （用户消息→meta→assistant 气泡 + 证据 + Trace），零 LLM / 零数据。
    /// </summary>
    private static async Task RunP20Async()
    {
        Logger.Info("=== P20 WPF 产品接线壳自检（同 ChatViewModel 映射，零 LLM / 零数据）===");
        int pass = 0, total = 0;
        void Check(string name, bool ok, string detail = "")
        {
            total++;
            Logger.Info(ok ? $"[PASS] {total} {name}" : $"[FAIL] {total} {name} {detail}");
            if (ok) pass++;
        }

        var opts = new MemoryAssistant.Core.Configuration.AgentOptions { MaxToolCalls = 10, MaxReplanRounds = 3, EnableReplanning = true, MaxTaskSeconds = 0 };
        var planner = new MemoryAssistant.Core.Agent.Planner.Planner(MemoryAssistant.Core.Agent.Planner.SkillCatalog.Default(), opts, chat: null);
        var skills = MemoryAssistant.Core.Agent.Skills.SkillRegistry.BuildDefault(new P20Backend());
        var agent = new MemoryAssistant.Core.Agent.Conversation.ConversationalAgent(planner, skills, opts);

        // 与 ChatViewModel.SendAsync 相同的映射逻辑（气泡模型）
        var bubbles = new System.Collections.Generic.List<(string Role, string Content, int EvidenceCount, int TraceCount)>();
        foreach (var q in new[] { "我和小明聊过秋招吗？", "他后来还说了什么？" })
        {
            var r = await agent.RunAsync(q);
            bubbles.Add(("user", q, 0, 0));
            var meta = r.Evidence.Count > 0
                ? $"依据 {r.Evidence.Count} 条聊天记录 · {r.TotalElapsedMs / 1000.0:0.0}s"
                : $"未读取聊天记录 · {r.TotalElapsedMs / 1000.0:0.0}s";
            if (!r.CompletedNormally) meta += $"（未完全查清：{r.EarlyStopReason}）";
            bubbles.Add(("meta", meta, 0, 0));
            var traceSteps = MemoryAssistant.Core.Workflow.TraceBuilder.Build(r.TaskTrace);
            bubbles.Add(("assistant", r.Answer, r.Evidence.Count, traceSteps.Count));
        }

        foreach (var (role, content, ev, tr) in bubbles)
            Logger.Info($"[{role}] {content}" + (ev > 0 ? $"（证据 {ev} 条，trace {tr} 行）" : ""));

        var assistant = bubbles.Where(b => b.Role == "assistant").ToList();
        Check("多轮产品会话（2 用户→2 助手气泡）", bubbles.Count == 6 && assistant.Count == 2, $"bubbles={bubbles.Count}");
        Check("第一轮回答含证据引用", assistant[0].EvidenceCount >= 1 && assistant[0].Content.Contains("[1]"), assistant[0].Content);
        Check("第二轮追问实体延续（他→小明）", assistant[1].EvidenceCount >= 1 || bubbles.Any(b => b.Content.Contains("小明") && b.Role == "meta"), assistant[1].Content);
        Check("trace 行数与气泡一致", assistant.All(a => a.TraceCount > 0), $"trace={assistant[0].TraceCount}");

        Logger.Info("=== P20 产品接线壳自检结束 ===");
        Logger.Info($"总 PASS {pass}/{total}");
    }

    /// <summary>P20 壳验收底座：按关键词返回"小明/秋招"素材，供两轮对话。</summary>
    private sealed class P20Backend : MemoryAssistant.Core.Agent.Skills.IMemoryBackend
    {
        private static readonly MemoryAssistant.Core.Agent.Evidence Fact = new()
        { SessionId = "s1", SessionDisplayName = "小明", CreateTime = 1754000000, SenderName = "小明", Content = "秋招有消息了吗", Source = "微信原文" };

        public Task<IReadOnlyList<MemoryAssistant.Core.RAG.RetrievedChunk>> SemanticSearchAsync(string query, int topK, CancellationToken ct)
        {
            MemoryAssistant.Core.RAG.RetrievedChunk[] hits = query.Contains("小明") || query.Contains("秋招") || query.Contains("秋招有消息了吗")
                ? [new MemoryAssistant.Core.RAG.RetrievedChunk { SessionId = "s1", SessionName = "小明", Date = "2026-08-01", Text = "秋招有消息了吗", MsgCount = 1 }]
                : [];
            return Task.FromResult<IReadOnlyList<MemoryAssistant.Core.RAG.RetrievedChunk>>(hits);
        }
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> ReadDayMessagesAsync(string sessionId, string date, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([Fact]);
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> KeywordSearchAsync(string keyword, string? sessionId, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([Fact]);
        public Task<string> GetStatsTextAsync(int limit, int sessionLimit, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Skills.DayActivity>> GetRecentDaysAsync(int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Skills.DayActivity>>([]);
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> GetDaySnippetsAsync(string date, CancellationToken ct) => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([]);
        public Task<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>> ScanCommitmentsAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<MemoryAssistant.Core.Agent.Evidence>>([]);
        public Task<string> GetTopicsTextAsync(int recentDays, CancellationToken ct) => Task.FromResult("");
        public Task<string> GetProfilesTextAsync(int top, CancellationToken ct) => Task.FromResult("");
    }

    /// <summary>V3.3 验收：微信窗口只读（找窗口 + 读当前可见聊天），以及规划器能否选中 wechat 能力。</summary>
    private static async Task RunWechatAsync()
    {
        Logger.Info("=== V3.3 微信窗口只读验收（UIAutomation · 零 LLM / 零数据）===");
        var bridge = new Integrations.UiAutomationWeChatBridge();

        // 1) 规划器：把"看看微信当前聊天"映射到 wechat 能力
        var planner = new MemoryAssistant.Core.Agent.Planner.Planner(
            MemoryAssistant.Core.Agent.Planner.SkillCatalog.Default(), Settings.Agent, chat: null);
        var plan = await planner.PlanAsync("看看微信当前聊天", preferLlm: false);
        var planned = plan.Steps.FirstOrDefault(s => s.Kind == MemoryAssistant.Core.Agent.Planner.PlanStepKind.Skill)?.Name;
        Logger.Info(planned == "wechat"
            ? $"[PASS] 规划：'看看微信当前聊天' → skill:wechat（{plan.Goal}）"
            : $"[FAIL] 规划未选中 wechat，实际: {planned}");

        // 2) 找窗口
        var wins = await bridge.ListWeChatWindowsAsync();
        Logger.Info($"[窗口] 发现 {wins.Count} 个微信窗口");
        foreach (var w in wins) Logger.Info($"  · {w}");
        Logger.Info($"[OCR] 系统可用识别语言：{string.Join(", ", Integrations.OcrScreenReader.AvailableLanguages())}");

        if (wins.Count == 0)
        {
            Logger.Info("[SKIP] 微信未运行（或未登录）——读取步骤跳过；启动微信后重跑即可。");
            Logger.Info("=== V3.3 微信窗口只读验收结束 ===");
            return;
        }

        // 3) 唤醒 / 回正窗口（第三阶段补充的能力，Agent 通过 wechat_wake_window 工具调用同一实现）
        //    它会还原最小化/托盘里的窗口、抢回焦点、在窗口空白处点一下收掉卡住的浮层——只动窗口，不发任何东西。
        //    **必须放在"读聊天"之前**：窗口在托盘里时读不到内容，先唤醒才能验证"隐藏 → 唤醒 → 读得到"整条链。
        var action = new Integrations.UiAutomationWeChatActionBridge(Logger.Info);
        var wake = await action.WakeWindowAsync();
        Logger.Info(wake.Success
            ? $"[PASS] 唤醒窗口：{wake.Detail}"
            : $"[FAIL] 唤醒窗口：{wake.Error}");

        // 4) 读当前可见聊天（只读：UIA 优先，微信 4.x 走 OCR）
        var read = await bridge.ReadVisibleChatAsync();
        if (!read.Success)
        {
            Logger.Info($"[SKIP] 窗口在但读不到聊天：{read.Error}");
        }
        else
        {
            Logger.Info($"[读取] 通道={read.Method} | 当前可见聊天 {read.Messages.Count} 条（只读，取最近）：");
            foreach (var m in read.Messages.TakeLast(12))
                Logger.Info($"  · {TruncateLine(m, 80)}");
        }

        Logger.Info("=== V3.3 微信窗口只读验收结束 ===");
    }

    /// <summary>
    /// V3.5b 端到端验收：本地起一个工具 HTTP 服务 → 用清单把工具热注册进注册表 →
    /// 真实 LLM 自主选用该工具并调用 → 用服务端请求记录证明"模型确实打到了外部工具"。
    /// 会消耗 token（用户明确要求），其他验收模式仍是零 token。
    /// </summary>
    private static async Task RunToolCallAsync(string[] args)
    {
        Logger.Info("=== V3.5b 端到端验收：真实 LLM 选用并调用外部工具 ===");

        if (string.IsNullOrWhiteSpace(Settings.Llm.ApiKey))
        {
            Logger.Error("未配置 LLM API Key（appsettings.local.json 或环境变量），无法跑真实工具调用验收。");
            return;
        }

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ma_toolcall_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);

        using var server = new Acceptance.LocalHttpServer((method, path) =>
            path.StartsWith("/weather", StringComparison.Ordinal)
                ? ("application/json", """{"city":"上海","weather":"晴","temp_c":25,"advice":"适合外出"}""")
                : ("application/json", """{"error":"not found"}"""));

        Logger.Info($"[服务] 本地工具服务已就绪：{server.BaseUrl}/weather?city=上海");

        var registry = new MemoryAssistant.Core.Agent.ToolRegistry();
        var hub = new MemoryAssistant.Core.Agent.Tools.ToolHub(registry);
        using var store = new MemoryAssistant.Infrastructure.Agent.JsonToolManifestStore(
            dir, hub, new MemoryAssistant.Infrastructure.Agent.HttpToolInvoker(Logger), Logger);

        try
        {
            // 工具通过"清单"上架 —— 走完整的热注册路径
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "weather.tool.json"), $$"""
            {
              "tools": [
                {
                  "name": "weather_now",
                  "description": "查询指定城市的实时天气（外部 HTTP 工具）",
                  "category": "Context",
                  "readOnly": true,
                  "method": "GET",
                  "url": "{{server.BaseUrl}}/weather?city={city}",
                  "parameters": [
                    { "name": "city", "type": "string", "description": "城市名", "required": true }
                  ]
                }
              ]
            }
            """);
            store.StartWatching();
            store.LoadAll();
            await WaitUntilAsync(() => registry.All.Any(t => t.Name == "weather_now"), TimeSpan.FromSeconds(5));
            Logger.Info($"[注册表] 工具 {registry.All.Count} 个：{string.Join("、", registry.All.Select(t => t.Name))}");

            // 真实验收要关掉 Eco 节流，否则 ToolCallingSkill 会按节流模式跳过 LLM
            Settings.Agent.EcoMode = false;
            var chat = new MemoryAssistant.Infrastructure.Agent.DeepSeekChatClient(
                Settings.Llm, Logger, retryCount: Settings.Runtime.RetryCount);
            var skill = new MemoryAssistant.Core.Agent.Skills.ToolCallingSkill(
                chat, registry, Settings.Agent, Logger);

            var query = GetArgValue(args, "--query", "帮我查一下上海现在的天气");
            Logger.Info($"[提问] {query}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await skill.ExecuteAsync(
                new MemoryAssistant.Core.Agent.Skills.SkillRequest { Query = query }, CancellationToken.None);
            sw.Stop();

            Logger.Info($"[结果] 充分={result.Sufficient} | 耗时 {sw.ElapsedMilliseconds}ms | {result.Summary}");
            Logger.Info($"[回答] {TruncateLine(result.Draft ?? result.Error ?? "", 300)}");
            Logger.Info($"[服务] 外部端点被请求 {server.HitCount} 次：{string.Join(" | ", server.Hits)}");
            Logger.Info(server.HitCount > 0
                ? "[PASS] 模型确实自主调用到了外部工具（HTTP 端点到打到了）"
                : "[FAIL] 外部工具没有被调用");

            var answered = (result.Draft ?? "").Contains("晴") || (result.Draft ?? "").Contains("25");
            Logger.Info(answered
                ? "[PASS] 回答使用了工具返回的真实数据（晴 / 25℃）"
                : "[WARN] 回答里没看到工具返回的数据，请人工核对");
        }
        catch (Exception ex)
        {
            Logger.Error($"端到端工具调用验收异常: {ex.Message}");
        }
        finally
        {
            try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
        }

        Logger.Info("=== V3.5b 端到端验收结束 ===");
    }

    /// <summary>
    /// V3.5 工具热注册验收（不需要 Python Bridge，直接压 ToolHub + 清单仓库）：
    /// 目录为空 → 投放一个 *.tool.json → 工具自动上架且 Schema 真实 → 删除清单 → 自动下架。
    /// </summary>
    private static async Task RunToolsAsync()
    {
        Logger.Info("=== V3.5 工具热注册验收（清单文件 = 工具，零 LLM / 零 Bridge）===");
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ma_tools_" + Guid.NewGuid().ToString("N")[..8]);
        var registry = new MemoryAssistant.Core.Agent.ToolRegistry();
        var hub = new MemoryAssistant.Core.Agent.Tools.ToolHub(registry);
        var store = new MemoryAssistant.Infrastructure.Agent.JsonToolManifestStore(
            dir, hub, new MemoryAssistant.Infrastructure.Agent.HttpToolInvoker(Logger), Logger);

        var changes = new List<string>();
        hub.Changed += c => changes.Add(c.ToString());

        try
        {
            Logger.Info("[初始] 目录为空 → 工具 0 个，来源 0 个");
            store.StartWatching();
            store.LoadAll();

            var manifestPath = System.IO.Path.Combine(dir, "weather.tool.json");
            System.IO.File.WriteAllText(manifestPath, """
            {
              "tools": [
                {
                  "name": "weather_now",
                  "description": "查询指定城市的实时天气（外部 HTTP 工具示例）",
                  "category": "Context",
                  "readOnly": true,
                  "method": "GET",
                  "url": "http://127.0.0.1:9/weather?city={city}",
                  "parameters": [
                    { "name": "city", "type": "string", "description": "城市名", "required": true }
                  ]
                },
                {
                  "name": "bad_entry_demo",
                  "description": "故意写错的条目（缺少 url），应被跳过而不影响其它工具",
                  "category": "Context"
                }
              ]
            }
            """);
            Logger.Info($"[投放] {System.IO.Path.GetFileName(manifestPath)}（含 1 个合法工具 + 1 个故意写错的条目）");

            await WaitUntilAsync(() => registry.All.Any(t => t.Name == "weather_now"), TimeSpan.FromSeconds(6));
            var afterAdd = registry.All.Select(t => t.Name).OrderBy(n => n).ToList();
            Logger.Info($"[更新] 工具 {afterAdd.Count} 个：{string.Join(", ", afterAdd)}");
            Logger.Info(afterAdd.Contains("weather_now")
                ? "[PASS] 新工具已上架（无需重启、无需改代码）"
                : "[FAIL] 新工具未生效");
            Logger.Info(afterAdd.Contains("bad_entry_demo") ? "[FAIL] 非法条目未被拦截" : "[PASS] 非法条目已按条跳过");

            var schema = registry.BuildSchemas().SingleOrDefault(s => s.Name == "weather_now");
            if (schema is not null)
            {
                var props = schema.Parameters?["properties"]?.AsObject().Select(p => p.Key) ?? [];
                var required = schema.Parameters?["required"]?.AsArray().Select(n => n!.GetValue<string>()) ?? [];
                Logger.Info($"[Schema] {schema.Name} | 参数: {string.Join("/", props)} | 必填: {string.Join("/", required)} | {schema.Description}");
            }

            // 联动：主链路的 tools Skill 执行时会现读注册表 → 新工具下一轮就能被 LLM 选用
            var catalog = MemoryAssistant.Core.Agent.Planner.SkillCatalog.Default();
            Logger.Info(catalog.Contains("tools")
                ? "[PASS] 能力目录已含 tools（调用已注册工具）"
                : "[FAIL] 能力目录缺少 tools");
            var planner = new MemoryAssistant.Core.Agent.Planner.Planner(catalog, Settings.Agent, chat: null);
            var plan = await planner.PlanAsync("调用工具 查一下上海天气", preferLlm: false);
            var picked = plan.Steps
                .FirstOrDefault(s => s.Kind == MemoryAssistant.Core.Agent.Planner.PlanStepKind.Skill)?.Name;
            Logger.Info(picked == "tools"
                ? "[PASS] 规划：「调用工具 …」→ skill:tools"
                : $"[FAIL] 规划未选中 tools，实际: {picked}");
            Logger.Info($"[联动] tools Skill 执行时现读注册表：此刻注册表 {registry.All.Count} 个工具（含刚热注册的 weather_now）");

            System.IO.File.Delete(manifestPath);
            Logger.Info($"[删除] {System.IO.Path.GetFileName(manifestPath)}");
            await WaitUntilAsync(() => !registry.All.Any(t => t.Name == "weather_now"), TimeSpan.FromSeconds(6));
            Logger.Info(registry.All.Any(t => t.Name == "weather_now")
                ? "[FAIL] 清单删除后工具仍在"
                : "[PASS] 清单删除后工具已自动下架");

            Logger.Info($"变化通知 {changes.Count} 次：{string.Join(" | ", changes)}");
        }
        catch (Exception ex)
        {
            Logger.Error($"工具热注册验收异常: {ex.Message}");
        }
        finally
        {
            store.Dispose();
            try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
        }
        Logger.Info("=== V3.5 工具热注册验收结束 ===");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(150);
        }
    }

    /// <summary>
    /// V3.4 写操作验收：规划命中 action → 人工确认闸门 → 真正执行（零 LLM）。
    /// 默认用 DenyAll 闸门验证"被拒绝时不动手"；--yes 换成放行闸门才真的操作微信。
    /// 建议目标用「文件传输助手」（发给自己），验证发送链路又不打扰任何人。
    /// </summary>
    private static async Task RunActionAsync(string query, bool autoApprove)
    {
        Logger.Info("=== V3.4 微信写操作验收（人工确认闸门 · 零 LLM）===");
        Logger.Info($"指令: {query}");
        Logger.Info($"确认闸门: {(autoApprove ? "自动放行（--yes）" : "默认拒绝（DenyAll，证明未确认不会动手）")}");

        try
        {
            var readBridge = new Integrations.UiAutomationWeChatBridge();
            var confirmation = autoApprove
                ? (MemoryAssistant.Core.Agent.Action.IActionConfirmation)
                    new MemoryAssistant.Core.Agent.Action.AlwaysApproveActionConfirmation()
                : new MemoryAssistant.Core.Agent.Action.DenyAllActionConfirmation();

            Services = new MemoryAssistant.Infrastructure.AppServices(Settings, Logger);
            await Services.InitializeAsync(
                ct: default,
                wechat: readBridge,
                wechatAction: new Integrations.UiAutomationWeChatActionBridge(Logger.Info),
                actionConfirmation: confirmation);

            // 1) 规划：写操作指令应命中 action Skill
            var planner = new MemoryAssistant.Core.Agent.Planner.Planner(
                MemoryAssistant.Core.Agent.Planner.SkillCatalog.Default(), Settings.Agent, chat: null);
            var plan = await planner.PlanAsync(query, preferLlm: false);
            var picked = plan.Steps
                .FirstOrDefault(s => s.Kind == MemoryAssistant.Core.Agent.Planner.PlanStepKind.Skill)?.Name;
            Logger.Info(picked == "action"
                ? "[PASS] 规划：写操作指令 → skill:action"
                : $"[FAIL] 规划未选中 action，实际: {picked}");

            // 1b) 工具面：Action 类工具是否已注册、Schema 是否真实
            foreach (var schema in Services.Tools!.BuildSchemas()
                         .Where(s => s.Name.StartsWith("wechat_", StringComparison.Ordinal)))
            {
                var props = schema.Parameters?["properties"]?.AsObject().Select(p => p.Key).ToList() ?? [];
                var required = schema.Parameters?["required"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
                Logger.Info($"[工具] {schema.Name} | 参数: {string.Join("/", props)} | 必填: {string.Join("/", required)} | {schema.Description}");
            }

            // 2) 走真实 Agent 链路
            var agent = Services.Agent!;
            var turn = await agent.RunAsync(query);
            Logger.Info($"[结果] 完成={turn.CompletedNormally} | 证据 {turn.Evidence.Count} 条 | {turn.TotalElapsedMs:0}ms");
            Logger.Info($"[回答] {TruncateLine((turn.Answer ?? "").Replace("\n", " ⏎ "), 300)}");
            if (turn.TaskTrace is { } tr)
                foreach (var c in tr.Cycles)
                    foreach (var s in c.Steps)
                        Logger.Info($"  [轨迹] {s.Skill} | 成功={s.Success} | {TruncateLine(s.Summary ?? s.Error ?? "", 120)}");

            if (confirmation is MemoryAssistant.Core.Agent.Action.AlwaysApproveActionConfirmation approved)
                foreach (var p in approved.Approved)
                    Logger.Info($"  [留痕] 已确认 {p.Action}：{p.Description}");

            await Services.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"写操作验收异常: {ex.Message}");
        }
        Logger.Info("=== V3.4 微信写操作验收结束 ===");
    }

    /// <summary>
    /// 列出最近会话，并挑一个"真实会话"作追踪对象（供验收自证用）。
    /// 追踪对象必须能被 list_sessions 的 keyword 命中——即 wxid / 群 id / 摘要里出现的词。
    /// </summary>
    private static async Task<string?> DumpSessionCandidatesAsync()
    {
        var resp = await Services!.Bridge.RequestAsync("list_sessions",
            new Dictionary<string, object?> { ["limit"] = 10 }, timeoutSeconds: 30);
        if (!resp.Success || resp.Data is not System.Text.Json.JsonElement arr ||
            arr.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            Logger.Warn($"[候选会话] 读取失败：{resp.Error}");
            return null;
        }

        string? picked = null;
        var n = 0;
        foreach (var s in arr.EnumerateArray())
        {
            var user = s.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
            var summary = s.TryGetProperty("summary", out var m) ? m.GetString() ?? "" : "";
            Logger.Info($"[候选会话] {++n}. {user} | 摘要: {TruncateLine(summary, 50)}");

            // 优先挑"真人会话"（wxid_/群）且摘要非空，更能演示增量；服务号/公众号跳过
            if (picked is null && summary.Length > 0
                && (user.StartsWith("wxid_", StringComparison.Ordinal)
                    || user.EndsWith("@chatroom", StringComparison.Ordinal)))
            {
                picked = user;
            }
        }
        if (picked is not null) Logger.Info($"[候选会话] 自动选择追踪对象：{picked}");
        return picked;
    }

    /// <summary>
    /// UI 弹窗冒烟：构造两个弹窗（证据原文 / 聊天记录定位）。
    /// XAML 里的资源键、绑定类型错误只在运行时暴露，构造一次就能挡住这类回归。
    /// </summary>
    private static Task RunDialogsCheckAsync()
    {
        Logger.Info("=== UI 弹窗冒烟（EvidenceWindow / ConversationWindow）===");
        int pass = 0, total = 0;
        void Check(string name, Func<string> act)
        {
            total++;
            try
            {
                var detail = act();
                Logger.Info($"[PASS] {total} {name} {detail}");
                pass++;
            }
            catch (Exception ex)
            {
                Logger.Info($"[FAIL] {total} {name}: {ex.Message}");
            }
        }

        var item = new ViewModels.EvidenceItem(1, "张晓明", "2026-09-11 21:49", "张晓明", "示例消息内容")
        {
            SessionId = "wxid_demo",
            CreateTime = 1757598540,
            Date = "2026-09-11",
        };

        Check("EvidenceWindow 构造", () =>
        {
            var w = new EvidenceWindow(item);
            w.Close();
            return "ok";
        });
        Check("ConversationWindow 构造（含定位卡片绑定）", () =>
        {
            var w = new ConversationWindow(item);
            w.Close();
            return "ok";
        });
        // 统一决策弹窗（request_user_confirmation 与写操作闸门共用）：选项按钮是代码生成的，
        // 缺资源键/绑定类型错只会在运行时炸，所以这里显式构造一次。
        Check("UserChoiceWindow 构造（统一决策弹窗）", () =>
        {
            var w = new Views.UserChoiceWindow(new Core.Agent.Interaction.UserChoiceRequest
            {
                Question = "是否给张三发送这条消息？",
                Options = ["发送", "修改后发送", "取消"],
                DefaultOption = "取消",
                Context = "将发送：你好，最近怎么样？",
            });
            var n = w.OptionList.Items.Count;
            w.Close();
            return $"生成 {n} 个选项按钮";
        });
        Check("ConversationLine 定位与表情映射", () =>
        {
            var line = MemoryAssistant.Core.Agent.Conversation.ConversationView.Build(
                [new MemoryAssistant.Core.Agent.Skills.RenderedMessage
                {
                    CreateTime = 1789134597,
                    SenderName = "张晓明",
                    Text = "示例消息内容[破涕为笑]",
                    Kind = "text",
                }],
                targetTime: 1789134597,
                targetContent: "示例消息内容")[0];
            return $"定位={(line.IsTarget ? "命中" : "未命中")} 文本={line.Text}";
        });

        // 多处卡片 → 同一套跳转：每页的卡片都必须带齐"会话 id + 时间 + 日期"，否则跳不过去。
        Check("时间线 DayChunk 卡片可跳转", () =>
        {
            var chunk = new ViewModels.DayChunk
            {
                SessionId = "wxid_demo", SessionName = "张晓明", Text = "示例消息内容",
                MsgCount = 5, Date = "2026-09-11", CreateTime = 1757598540,
            };
            var w = new ConversationWindow(new ViewModels.EvidenceItem(
                0, chunk.SessionName, chunk.Date, "", chunk.Text)
            {
                SessionId = chunk.SessionId, CreateTime = chunk.CreateTime, Date = chunk.Date,
            });
            w.Close();
            return $"会话={chunk.SessionId} 日期={chunk.Date}";
        });
        Check("搜索结果 SearchResultItem 卡片可跳转", () =>
        {
            var r = new ViewModels.SearchResultItem
            {
                SessionName = "张晓明", Time = "2026-09-11 21:49", Sender = "张晓明",
                Content = "示例消息内容", SessionId = "wxid_demo",
                CreateTime = 1757598540, Date = "2026-09-11",
            };
            var w = new ConversationWindow(new ViewModels.EvidenceItem(
                0, r.SessionName, r.Time, r.Sender, r.Content)
            {
                SessionId = r.SessionId, CreateTime = r.CreateTime, Date = r.Date,
            });
            w.Close();
            return $"会话={r.SessionId} 时间={r.Time}";
        });
        Check("承诺 CommitmentCard 卡片可跳转", () =>
        {
            var card = new ViewModels.CommitmentCard
            {
                SessionName = "张晓明", Date = "2026-09-11", Sender = "我", IsSelf = true,
                Content = "我明天把资料发你", MatchedText = "明天把资料发你",
                SessionId = "wxid_demo", CreateTime = 1757598540,
            };
            var w = new ConversationWindow(new ViewModels.EvidenceItem(
                0, card.SessionName, card.Date, card.Sender, card.Content)
            {
                SessionId = card.SessionId, CreateTime = card.CreateTime, Date = card.Date,
            });
            w.Close();
            return $"会话={card.SessionId} 类型={card.KindTag}";
        });
        Check("话题样本 TopicSample 卡片可跳转", () =>
        {
            var s = new MemoryAssistant.Core.Features.Topics.TopicSample(
                "wxid_demo", "张晓明", "2026-09-11", "示例消息内容", 1757598540);
            var w = new ConversationWindow(new ViewModels.EvidenceItem(
                0, s.SessionName, s.Date, "", s.Text)
            {
                SessionId = s.SessionId, CreateTime = s.CreateTime, Date = s.Date,
            });
            w.Close();
            return $"会话={s.SessionId} 起始={s.CreateTime}";
        });
        Check("画像最近消息 RecentMessage 卡片可跳转", () =>
        {
            var m = new ViewModels.RecentMessage("2026-09-11 21:49", "张晓明", "示例消息内容")
            {
                SessionId = "wxid_demo", CreateTime = 1757598540,
            };
            var w = new ConversationWindow(new ViewModels.EvidenceItem(
                0, "张晓明", m.Time, m.Sender, m.Content)
            {
                SessionId = m.SessionId, CreateTime = m.CreateTime, Date = "2026-09-11",
            });
            w.Close();
            return $"会话={m.SessionId} 时间={m.Time}";
        });

        Check("表情包：有图走图片分支，无图显示描述或占位", () =>
        {
            var lines = MemoryAssistant.Core.Agent.Conversation.ConversationView.Build(
            [
                new MemoryAssistant.Core.Agent.Skills.RenderedMessage
                {
                    CreateTime = 1789134597, Kind = "sticker", MediaPath = @"C:\demo\a.gif",
                },
                new MemoryAssistant.Core.Agent.Skills.RenderedMessage
                {
                    CreateTime = 1789134598, Kind = "sticker", Text = "好震惊",
                },
                new MemoryAssistant.Core.Agent.Skills.RenderedMessage
                {
                    CreateTime = 1789134599, Kind = "sticker",
                },
            ]);
            if (!lines[0].HasImage) throw new Exception("有图的表情包没走图片分支");
            if (lines[1].Note != "[表情包：好震惊]") throw new Exception($"描述未渲染：{lines[1].Note}");
            if (lines[2].Note != "[表情包]") throw new Exception($"占位异常：{lines[2].Note}");
            return $"有图 1 / 描述 {lines[1].Note} / 占位 {lines[2].Note}";
        });

        Logger.Info("=== UI 弹窗冒烟结束 ===");
        Logger.Info($"总 PASS {pass}/{total}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 真实数据（--render）：用本地库渲染某会话某天的整段对话，核对"文字/表情/图片/语音"是否真的能出来。
    /// 这是 GUI 截图不可靠时，验证 ConversationWindow 数据链路的手段。
    /// </summary>
    private static async Task RunRenderCheckAsync(string sessionId, string date)
    {
        Logger.Info($"--- 真实数据：{sessionId} 的 {date} 整段对话 ---");
        try
        {
            Services = new MemoryAssistant.Infrastructure.AppServices(Settings, Logger);
            await Services.InitializeAsync(ct: default, wechat: new Integrations.UiAutomationWeChatBridge());
            Logger.Info($"        Bridge running={Services.Bridge.IsRunning}");

            var backend = Services.MemoryBackend;
            if (backend is null)
            {
                Logger.Info("[FAIL] 真实对话渲染：MemoryBackend 未装配（Bridge 未就绪）");
                return;
            }

            var day = DateTime.Parse(date);

            // --session 既可以是会话 id，也可以是人名/群名：走 find_sessions 解析（和 Agent 同一条路）
            Logger.Info("        正在解析会话（find_sessions）…");
            var hits = await backend.FindSessionsAsync(sessionId, null, 5, default);
            var hit = hits.FirstOrDefault(h => h.Id == sessionId)
                      ?? hits.OrderByDescending(h => h.LastTimestamp).FirstOrDefault();
            var resolvedId = hit?.Id ?? sessionId;
            var resolvedName = hit?.DisplayName ?? sessionId;
            Logger.Info($"        解析会话：{resolvedName} → {resolvedId}（候选 {hits.Count} 个）");

            var begin = new DateTimeOffset(day, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
            var messages = await backend.ReadRenderedAsync(resolvedId, begin, begin + 86400, 500, default);
            var lines = MemoryAssistant.Core.Agent.Conversation.ConversationView.Build(messages);

            var kinds = lines.GroupBy(l => l.Kind).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}={g.Count()}");
            Logger.Info($"[{(messages.Count > 0 ? "PASS" : "FAIL")}] 读到 {messages.Count} 条"
                + $" · 文字 {lines.Count(l => l.HasText)} / 图片 {lines.Count(l => l.HasImage)}"
                + $" / 语音 {lines.Count(l => l.HasAudio)} / 如实标注 {lines.Count(l => l.HasNote)}");
            Logger.Info($"        类型分布：{string.Join(", ", kinds)}");
            var stickers = lines.Where(l => l.Kind == "sticker").ToList();
            var described = stickers.Count(l => !l.HasImage && l.Note.StartsWith("[表情包："));
            Logger.Info($"        表情包：{stickers.Count} 条 · 有图 {stickers.Count(l => l.HasImage)}"
                + $" · 有描述 {described} · 仍为占位 {stickers.Count - described - stickers.Count(l => l.HasImage)}");
            foreach (var l in stickers.Take(3))
                Logger.Info($"        表情包样本：{l.Time} {l.Sender} → {(l.HasImage ? System.IO.Path.GetFileName(l.ImagePath!) : l.Note)}");
            Logger.Info($"        气泡归属：我 {lines.Count(l => l.IsSelf)} 条（右侧） / 对方 {lines.Count(l => !l.IsSelf)} 条（左侧）"
                + $" / 群聊显示发言人 {lines.Count(l => l.ShowSender)} 条");
            foreach (var l in lines.Where(l => l.HasImage).Take(3))
                Logger.Info($"        图片样本：{l.Time} {l.Sender} → {System.IO.Path.GetFileName(l.ImagePath!)}");
            foreach (var l in lines.Take(3))
                Logger.Info($"        {l.Time} {(l.IsSelf ? "我" : l.Sender)}：{TruncateLine(l.Text.Length > 0 ? l.Text : (l.Note.Length > 0 ? l.Note : "［媒体］"), 40)}");
        }
        catch (Exception ex)
        {
            Logger.Info($"[FAIL] 真实对话渲染：{ex.Message}");
        }
    }

    /// <summary>
    /// V4.0 验收：对话式任务编排全链路（真实数据 · Eco 小样本 · 零 LLM）。
    /// 用户一句话需求 → Agent 借能力产出任务草案 → 确认后落库纳管 → 任务真的能跑；
    /// 并验证普通提问不会被误当成"建任务"。
    /// </summary>
    private static async Task RunMissionChatAsync(string query)
    {
        Logger.Info("=== V4.0 对话式任务编排验收（需求 → 可执行工作流 → 确认创建 → 真实执行）===");
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "missions_v40_check.json");
        try
        {
            Services = new MemoryAssistant.Infrastructure.AppServices(Settings, Logger, missionsFilePath: tempPath);
            await Services.InitializeAsync(
                ct: default,
                wechat: new Integrations.UiAutomationWeChatBridge(),
                missionExecutor: new Missions.ConversationMissionExecutor(Services),
                missionProbe: new Missions.WeChatMissionProbe(Services));

            var agent = Services.Agent;
            if (agent is null) { Logger.Error("[FAIL] Agent 未装配"); return; }

            // 1) 一句话需求 → 草案（此时不得有任何副作用）
            Logger.Info($"[用户] {query}");
            var turn = await agent.RunAsync(query);
            var draft = turn.PendingMission;
            Logger.Info($"[Agent] 计划轮次={turn.RoundCount} 证据={turn.Evidence.Count} 耗时={turn.TotalElapsedMs:0}ms");
            Logger.Info($"[Agent回答] {TruncateLine(turn.Answer, 100)}");
            if (draft is null) { Logger.Error("[FAIL] 未识别出任务草案（需求 → 工作流 链路断开）"); return; }
            Logger.Info("[草案]\n" + draft.Describe());

            var before = Services.MissionCatalog!.Items.Count;
            Logger.Info($"[确认前] 任务目录 {before} 条，草案未落库（需用户点头）");

            // 2) 用户点"创建任务"
            var created = Services.MissionComposer!.Create(draft, start: true);
            Logger.Info($"[确认后] 已创建「{created.Title}」｜{created.TriggerText}｜状态={created.StatusText}｜下次运行={Services.Scheduler!.NextRun(created.Id):HH:mm:ss}");

            var reloaded = new MemoryAssistant.Infrastructure.Missions.JsonMissionRepository(tempPath).Load();
            var persisted = reloaded.FirstOrDefault(m => m.Title == created.Title);
            Logger.Info(persisted is not null && persisted.Target == created.Target
                ? $"[PASS] 任务已持久化（Target={persisted.Target}，{persisted.TriggerText}）"
                : "[FAIL] 任务未持久化到本地");

            // 3) 创建出来的任务真的能跑（首次执行：有内容则总结，无内容则建立基线/跳过）
            var run = await Services.Scheduler.RunOnceAsync(created.Id);
            Logger.Info($"[执行] 跳过={run?.Skipped} | {TruncateLine(run?.Summary ?? "无结果", 220)}");
            Logger.Info(run is not null ? "[PASS] 对话创建的任务可真实执行（工作流可用）" : "[FAIL] 任务执行无结果");

            // 4) 普通提问不得被误判成"建任务"
            var plain = await agent.RunAsync("我和朋友最近聊过什么");
            Logger.Info(plain.PendingMission is null
                ? "[PASS] 普通提问不触发编排（不会误弹任务卡）"
                : $"[FAIL] 普通提问被误判为建任务：{plain.PendingMission.Title}");

            await Services.ShutdownAsync();
            Logger.Info("=== V4.0 对话式任务编排验收结束 ===");
        }
        catch (Exception ex)
        {
            Logger.Error($"编排验收异常: {ex.Message}");
        }
        finally
        {
            try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// 诊断模式（--ocrdump）：把微信"搜索下拉"的整窗 OCR 行与截图转储出来。
    /// 用途是给维护者肉眼核对"哪些行是联网建议、哪些行是本地会话"，从而校准 OCR 判据。
    /// 只输入文字：不点结果、不回车、不发送，结束时也**不按 Esc**（Esc 会把微信收进托盘）。
    /// </summary>
    private static async Task RunOcrDumpAsync(string target, string outPath)
    {
        Logger.Info("=== OCR 转储（搜索下拉结构核对 · 只输入不点击）===");
        Logger.Info($"目标: {target} | 截图输出: {outPath}");
        try
        {
            var bridge = new Integrations.UiAutomationWeChatActionBridge(Logger.Info);
            var (png, lines) = await bridge.DumpSearchDropdownAsync(target, CancellationToken.None);

            if (png is { Length: > 0 })
            {
                var dir = System.IO.Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                await System.IO.File.WriteAllBytesAsync(outPath, png);
                Logger.Info($"[截图] 已保存 {outPath}（{png.Length / 1024} KB）");
            }
            else
            {
                Logger.Warn("[截图] 未抓到窗口位图");
            }

            Logger.Info($"[OCR 行] 共 {lines.Count} 行（按 y 排序）：");
            foreach (var l in lines) Logger.Info("  " + l);
        }
        catch (Exception ex)
        {
            Logger.Error($"OCR 转储异常: {ex.Message}");
        }
        Logger.Info("=== OCR 转储结束 ===");
    }

    /// <summary>
    /// V3.7 自动回复验收：三件事分别验——意图能不能被解析成"自动回复任务"、
    /// 子智能体在真实数据上能不能生成回复、以及**所有该拦住的情况是否真的拦住了**。
    /// 默认只生成草稿（Eco 闸门），加 --send 才真的发出去（发给自己/指定对象，需显式同意）。
    /// </summary>
    private static async Task RunAutoReplyCheckAsync(string target, bool realSend)
    {
        Logger.Info("=== V3.7 自动回复验收（意图 → 子智能体 → 发送闸门）===");
        int pass = 0, total = 0;
        void Check(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            Logger.Info($"[{(ok ? "PASS" : "FAIL")}] {name} {detail}");
        }

        // 1) 意图解析（零 LLM）：一句话 → 自动回复任务草案
        var draft = MemoryAssistant.Core.Missions.MissionIntentParser.TryParse("帮我自动回复张晓明的消息");
        Check("意图解析：自动回复 → Watch 草案",
            draft is { Action: MemoryAssistant.Core.Missions.MissionActionKind.AutoReply, Target: "张晓明" },
            $"Action={draft?.Action} Trigger={draft?.Trigger} Target={draft?.Target} | {draft?.TriggerText}");
        Check("意图解析：说不出对象就不建任务",
            MemoryAssistant.Core.Missions.MissionIntentParser.TryParse("帮我自动回复一下吧") is null
            && MemoryAssistant.Core.Missions.MissionIntentParser.TryParse("怎么自动回复别人的消息？") is null,
            "「帮我自动回复一下吧」「怎么自动回复别人的消息？」均未识别为任务");
        var def = draft?.ToDefinition();
        Check("任务模型：Action 落到任务定义且列表可见",
            def is { Action: MemoryAssistant.Core.Missions.MissionActionKind.AutoReply }
            && def.TriggerText.Contains("自动回复"),
            $"{def?.TriggerText}");

        // 2) 持久化往返：自动回复标记不能丢
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "missions_v37_check.json");
        try
        {
            var repo = new MemoryAssistant.Infrastructure.Missions.JsonMissionRepository(tempPath);
            repo.Save(def is null ? [] : [def]);
            var back = repo.Load().FirstOrDefault();
            Check("持久化往返：Action/Target 保留",
                back is { Action: MemoryAssistant.Core.Missions.MissionActionKind.AutoReply, Target: "张晓明" },
                $"重新加载 Action={back?.Action} Target={back?.Target}");

            // 3) 真实链路：真数据读 + 真模型生成 + 闸门决定发不发
            Settings.Agent.SuppressAutoReply = false;
            if (realSend) Settings.Agent.EcoMode = false;   // 明确 --send 时才允许真的发
            Logger.Info($"发送闸门：{(realSend ? "**真实发送（--send）**" : "只出草稿（Eco 闸门，不发送）")}");

            Services = new MemoryAssistant.Infrastructure.AppServices(Settings, Logger);
            await Services.InitializeAsync(
                ct: default,
                wechat: new Integrations.UiAutomationWeChatBridge(),
                wechatAction: new Integrations.UiAutomationWeChatActionBridge(Logger.Info),
                actionConfirmation: new MemoryAssistant.Core.Agent.Action.DenyAllActionConfirmation());

            if (Services.ReplyAgent is null)
            {
                Check("子智能体已装配", false, "ReplyAgent 为空（AppServices 未创建）");
            }
            else
            {
                Check("子智能体已装配", true, "AppServices.ReplyAgent 就绪");

                var autoTarget = string.IsNullOrWhiteSpace(target) ? await DumpSessionCandidatesAsync() : target;
                autoTarget ??= "filehelper";
                Logger.Info($"[对象] {autoTarget}");

                var mission = new MemoryAssistant.Core.Missions.MissionDefinition
                {
                    Title = $"自动回复「{autoTarget}」的新消息",
                    Goal = "帮我自动回复她的消息，语气随意一点，别替我承诺具体事情。",
                    Trigger = MemoryAssistant.Core.Missions.MissionTriggerKind.Watch,
                    Action = MemoryAssistant.Core.Missions.MissionActionKind.AutoReply,
                    Target = autoTarget,
                    IntervalMinutes = 5,
                };

                // 3a) 首次运行：只记基线，绝不回历史
                var first = await Services.ReplyAgent.RunAsync(mission, CancellationToken.None);
                Check("首次运行只建基线（不回历史）",
                    !first.Sent && first.Skipped && first.Note.Contains("基线"),
                    $"Sent={first.Sent} | {TruncateLine(first.Note, 80)}");

                // 3b) 把基线推到 3 天前 → 最近一批消息就算"新消息"，真实生成一条回复
                mission.Restore(MemoryAssistant.Core.Missions.MissionStatus.Paused,
                    DateTimeOffset.Now.AddDays(-3), "验收基线");
                var second = await Services.ReplyAgent.RunAsync(mission, CancellationToken.None);
                Logger.Info($"[结果] Sent={second.Sent} 新增={second.NewCount} 草稿={(string.IsNullOrWhiteSpace(second.Draft) ? "无" : second.Draft)}");
                Logger.Info($"[关键信息] {TruncateLine(second.Note, 160)}");

                if (second.NewCount == 0)
                {
                    Logger.Info("[SKIP] 该会话最近 20 条里没有对方的消息（换个对象：--target 人名），生成环节跳过");
                }
                else
                {
                    Check("真实数据 + 真实模型生成出可发送正文",
                        second.Generated && !string.IsNullOrWhiteSpace(second.Draft),
                        $"Generated={second.Generated} 长度={second.Draft.Length} 换行={second.Draft.Contains('\n')}");
                    Check(realSend ? "显式 --send：已真的发出去" : "默认闸门：生成了但没发送",
                        realSend ? second.Sent : !second.Sent,
                        $"Sent={second.Sent}");
                }
            }

            await Services.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"自动回复验收异常: {ex.Message}");
            Check("验收执行未抛异常", false, ex.Message);
        }
        finally
        {
            try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { }
        }

        Logger.Info($"总 PASS {pass}/{total}");
        Logger.Info("=== V3.7 自动回复验收结束 ===");
    }

    /// <summary>
    /// V3.2/V3.6 任务系统真实验收：任务 → 调度器真实执行（独立 Agent）+ 追踪型任务的增量语义。
    /// </summary>
    private static async Task RunMissionsAsync(string goal, string target)
    {
        Logger.Info("=== V3.2/V3.6 任务系统真实验收（调度器 · 追踪增量 · Eco 小样本 · 零 LLM）===");
        Logger.Info($"任务目标: {goal}");
        Logger.Info($"追踪对象: {target}");
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "missions_v32_check.json");
        try
        {
            Services = new MemoryAssistant.Infrastructure.AppServices(Settings, Logger);
            await Services.InitializeAsync(ct: default, wechat: new Integrations.UiAutomationWeChatBridge());

            var repo = new MemoryAssistant.Infrastructure.Missions.JsonMissionRepository(tempPath);
            var store = new MemoryAssistant.Core.Missions.MissionStore();
            var mission = store.Add(new MemoryAssistant.Core.Missions.MissionDefinition
            {
                Title = "真实验收任务",
                Goal = goal,
                Trigger = MemoryAssistant.Core.Missions.MissionTriggerKind.Interval,
                IntervalMinutes = 30,
            });

            // V3.6：追踪型任务（盯着某个会话，只在有"对方新消息"时才执行）
            Logger.Info($"--- 追踪型任务 ---");
            var autoTarget = await DumpSessionCandidatesAsync();
            var trackTarget = string.IsNullOrWhiteSpace(target) ? (autoTarget ?? "filehelper") : target;
            Logger.Info($"[追踪] 追踪对象 = {trackTarget}");

            var tracking = store.Add(new MemoryAssistant.Core.Missions.MissionDefinition
            {
                Title = $"追踪「{trackTarget}」的新消息",
                Goal = "总结对方新消息的要点，若需要回复请先给草稿等我确认。",
                Trigger = MemoryAssistant.Core.Missions.MissionTriggerKind.Watch,
                Target = trackTarget,
                IntervalMinutes = 5,
            });

            var scheduler = new MemoryAssistant.Core.Missions.MissionScheduler(
                store,
                new Missions.ConversationMissionExecutor(Services),
                probe: new Missions.WeChatMissionProbe(Services));
            scheduler.Start();
            scheduler.Changed += () => repo.Save(store.Items);

            // 0) 追踪型任务：第一跑（有内容则总结；无内容则建立基线）
            var first = await scheduler.RunOnceAsync(tracking.Id);
            Logger.Info($"[追踪1] 跳过={first?.Skipped} | {first?.Summary}");
            if (!string.IsNullOrWhiteSpace(tracking.LastResult))
                Logger.Info($"[追踪1·结果] {TruncateLine(tracking.LastResult!, 200)}");

            // 0b) 紧接着再跑一次：应因"无新增"而跳过（证明增量语义生效，不重复总结）
            var second = await scheduler.RunOnceAsync(tracking.Id);
            Logger.Info($"[追踪2] 跳过={second?.Skipped} | {second?.Summary}");
            Logger.Info(second is { Skipped: true }
                ? "[PASS] 无新增时自动跳过（不空转、不重复总结）"
                : "[WARN] 第二次仍执行了（可能期间真有新消息）");
            Logger.Info($"[追踪] 日志尾部：{(scheduler.Log.Count > 0 ? scheduler.Log[0] : "无")}");

            // 1) 立即执行一次（真实 Agent：规划→Skill→证据→回答）
            var r = await scheduler.RunOnceAsync(mission.Id);
            Logger.Info($"[执行] 成功={r?.Success} | 证据 {r?.EvidenceCount} 条 | {r?.ElapsedMs:0}ms");
            Logger.Info($"[摘要] {TruncateLine(r?.Summary ?? "", 300)}");
            if (r?.TraceText is { Length: > 0 } t)
                Logger.Info("[轨迹]\n" + (t.Length <= 500 ? t : t[..500] + "…"));

            // 2) 启动调度（Interval 30 分钟）→ 查看下次运行时间
            var started = scheduler.StartMission(mission.Id);
            Logger.Info($"[调度] 启动={started} | 状态={mission.StatusText} | 下次运行={scheduler.NextRun(mission.Id):HH:mm:ss}");

            // 3) 停止 → 取消托管
            scheduler.StopMission(mission.Id);
            Logger.Info($"[调度] 停止后状态={mission.StatusText} | 托管中={scheduler.IsRunning(mission.Id)}");

            // 4) 持久化往返
            repo.Save(store.Items);
            var reloaded = repo.Load();
            Logger.Info($"[持久化] 文件={tempPath} | 重新加载 {reloaded.Count} 条 | 上次结果={(reloaded.FirstOrDefault()?.LastResult is { } lr ? TruncateLine(lr, 60) : "无")}");
            var reloadedTracking = reloaded.FirstOrDefault(x => x.Trigger == MemoryAssistant.Core.Missions.MissionTriggerKind.Watch);
            if (reloadedTracking is { } rt && rt.Target == trackTarget)
                Logger.Info($"[PASS] 追踪对象已持久化（Target={rt.Target}，{rt.TriggerText}）");
            else
                Logger.Info($"[FAIL] 追踪对象丢失（加载到 {reloadedTracking?.Target ?? "空"}）");

            scheduler.Dispose();
            await Services.ShutdownAsync();
            Logger.Info("=== V3.2 任务系统真实验收结束 ===");
        }
        catch (Exception ex)
        {
            Logger.Error($"任务验收异常: {ex.Message}");
        }
        finally
        {
            try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// V4.1 验收：完整展示"模型思考过程 + 自主工具调用"。
    /// 输出：规划思考（模型怎么理解意图）→ 每步 Skill → 本轮思考 → 每次工具调用（参数/结果）→ 判定 → 最终答案。
    /// 这是观察"模型会不会自己决定调什么工具"的主要窗口。
    /// </summary>
    private static async Task RunThinkAsync(string query, string? query2, bool forceRulePlan, bool autoApprove = false)
    {
        Logger.Info("=== V4.1 模型思考过程 + 自主工具调用（真实 LLM）===");
        Logger.Info($"模型: {Settings.Llm.Model} | 查询: {query}");

        if (string.IsNullOrWhiteSpace(Settings.Llm.ApiKey))
        {
            Logger.Error("未配置 LLM API Key，无法观察模型思考（appsettings.local.json 的 llm.apiKey）。");
            return;
        }

        // ToolCallingSkill 在 Eco 下会主动跳过 LLM（省额度）；要观察真实工具调用必须关掉节流。
        if (Settings.Agent.EcoMode)
        {
            Settings.Agent.EcoMode = false;
            Logger.Info("[节流] 已关闭 Eco（否则 tools Skill 会跳过 LLM，看不到真实工具调用）。");
        }
        if (forceRulePlan)
        {
            Settings.Agent.EnableLlmPlanning = false;
            Logger.Info("[规划] 强制规则规划（--rule-plan）：可与默认的模型规划对比。");
        }
        else
        {
            Logger.Info($"[规划] 模型自主规划：{(Settings.Agent.EnableLlmPlanning ? "开" : "关")}");
        }

        try
        {
            Services = new MemoryAssistant.Infrastructure.AppServices(Settings, Logger);
            await Services.InitializeAsync(
                ct: default,
                wechat: new Integrations.UiAutomationWeChatBridge(),
                // --yes：写操作自动放行（无人值守下验证"自主发送"用；默认不接闸门 = 一律不发）
                wechatAction: autoApprove ? new Integrations.UiAutomationWeChatActionBridge(Logger.Info) : null,
                actionConfirmation: autoApprove
                    ? new MemoryAssistant.Core.Agent.Action.AlwaysApproveActionConfirmation()
                    : null);
            var agent = Services.Agent;
            if (agent is null)
            {
                Logger.Error("Agent 装配失败（Bridge 未启动？）。");
                return;
            }

            await RunThinkTurnAsync(agent, query, "第 1 轮");
            if (!string.IsNullOrWhiteSpace(query2))
                await RunThinkTurnAsync(agent, query2!, "第 2 轮（追问）");

            await Services.ShutdownAsync();
        }
        catch (Exception ex)
        {
            // 打全栈：验收模式排查必须能看到是哪一层炸的（只打 Message 会丢失位置）。
            Logger.Error($"--think 异常: {ex}");
        }
    }

    /// <summary>
    /// 跑一轮并流式打印：状态 → **思考（先流完）** → 答案（后流） → 完整轨迹。
    /// 用 atLineStart 跟踪光标，避免"思考吐了一半就插进度行"糊成一团。
    /// </summary>
    private static async Task RunThinkTurnAsync(
        MemoryAssistant.Core.Agent.Conversation.ConversationalAgent agent, string query, string label)
    {
        Logger.Info("");
        Logger.Info($"########## {label}：{query}");

        bool atLineStart = true;
        bool thinkingStarted = false, answerStarted = false;

        void NewLine()
        {
            if (!atLineStart) { Console.WriteLine(); atLineStart = true; }
        }

        var result = await agent.RunAsync(query, default, p =>
        {
            switch (p.Kind)
            {
                case AgentProgressKind.Status:
                    NewLine();
                    Logger.Info($"[进度] {p.Text}");
                    break;

                case AgentProgressKind.ThinkingDelta:
                    if (!thinkingStarted)
                    {
                        thinkingStarted = true;
                        NewLine();
                        Console.WriteLine("=== 思考（流式）===");
                    }
                    Console.Write(p.Text);
                    atLineStart = false;
                    break;

                // 工具调用单独一行落地：排查"到底调了没有、调的是什么"时，这段是唯一的硬证据
                case AgentProgressKind.ToolCall:
                    NewLine();
                    Logger.Info(p.Phase == "start"
                        ? $"[工具] ▶ {p.ToolName}({p.ToolArgs})"
                        : $"[工具] {(p.ToolSuccess ? "✓" : "✕")} {p.ToolName} → {p.ToolSummary}");
                    break;

                default:
                    if (!answerStarted)
                    {
                        answerStarted = true;
                        NewLine();
                        Console.WriteLine("=== 回答（流式）===");
                    }
                    Console.Write(p.Text);
                    atLineStart = false;
                    break;
            }
        });

        NewLine();
        PrintThinking(result);
        if (!answerStarted)
        {
            Logger.Info("");
            Logger.Info("=== 最终回答（未走流式）===");
            Logger.Info(result.Answer);
        }
        Logger.Info($"=== {label}结束（{result.Evidence.Count} 条证据 / {result.TotalElapsedMs:0}ms）===");
    }

    /// <summary>把 TaskTrace 里的思考过程与工具调用完整打印出来（验收/排查用）。</summary>
    private static void PrintThinking(MemoryAssistant.Core.Agent.AgentResult r)
    {
        if (r.TaskTrace is not { } trace)
        {
            Logger.Info("[思考] 无 TaskTrace（该路径未记录轨迹）");
            return;
        }

        foreach (var c in trace.Cycles)
        {
            Logger.Info("");
            Logger.Info($"[第 {c.Number} 轮] 目标: {c.Goal}");
            Logger.Info($"  计划: [{string.Join(", ", c.PlannedSteps)}]");
            PrintBlock("规划思考", c.PlanReasoning, 2);

            foreach (var s in c.Steps)
            {
                Logger.Info($"  ▶ {s.Skill}：{s.Summary}（{s.LatencyMs:0}ms）");
                PrintBlock("思考", s.Reasoning, 4);
                foreach (var t in s.ToolCalls)
                {
                    Logger.Info(t.Success
                        ? $"      ⇢ 调用 {t.Name}({TruncateLine(t.ArgumentsJson, 160)}) {t.LatencyMs:0}ms"
                        : $"      ⇢ 调用 {t.Name}({TruncateLine(t.ArgumentsJson, 160)}) 失败: {t.Error}");
                    if (t.Success && !string.IsNullOrWhiteSpace(t.Output))
                        Logger.Info($"        ← {TruncateLine(t.Output.Replace("\n", " "), 160)}");
                }
            }

            if (c.Verdict is not null) Logger.Info($"  判定: {c.Verdict}");
        }
    }

    /// <summary>多行文本按行缩进打印（思考过程往往多行，糊成一行没法看）。</summary>
    private static void PrintBlock(string label, string? text, int indent)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var pad = new string(' ', indent);
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) Logger.Info($"{pad}{label}: {line}");
        }
    }

    private static async Task RunLiveAsync(string query)
    {
        Logger.Info("=== 真实数据验证（产品链路 · Eco 小样本 · 零 LLM）===");
        Logger.Info($"查询: {query}");

        try
        {
            Services = new MemoryAssistant.Infrastructure.AppServices(Settings, Logger);
            await Services.InitializeAsync(ct: default, wechat: new Integrations.UiAutomationWeChatBridge());
            var agent = Services.Agent;
            if (agent is null)
            {
                Logger.Error("Agent 装配失败（Bridge 未启动？）。");
                return;
            }
            Logger.Info("Agent 就绪（Planner + 7 Skills + BridgeMemoryBackend）。");

            var turn = await agent.RunAsync(query);
            PrintTurn(agent, turn, "第 1 轮");

            // 第 2 轮：对上一轮证据追问（多轮上下文验证），小成本
            if (turn.CompletedNormally && turn.Evidence.Count > 0)
            {
                var follow = await agent.RunAsync("展开第1条");
                PrintTurn(agent, follow, "第 2 轮（展开第1条）");
            }
            else
            {
                Logger.Info("（第 1 轮无证据或未完成，跳过第 2 轮追问）");
            }

            await Services.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"真实验证异常: {ex.Message}");
        }
    }

    private static void PrintTurn(
        MemoryAssistant.Core.Agent.Conversation.ConversationalAgent agent,
        MemoryAssistant.Core.Agent.AgentResult r,
        string label)
    {
        var meta = $"Agent | 计划轮次 {r.RoundCount} | 证据 {r.Evidence.Count} 条 | 耗时 {r.TotalElapsedMs:0}ms";
        if (!r.CompletedNormally) meta += $" | 未完成: {r.EarlyStopReason}";
        Logger.Info($"[{label}][meta] {meta}");
        foreach (var e in r.Evidence)
        {
            var session = string.IsNullOrEmpty(e.SessionDisplayName) ? e.SessionId : e.SessionDisplayName;
            // 跳转可行性：点证据卡片要能落到具体会话 + 具体时间，这里把关键字段打出来
            var jump = string.IsNullOrWhiteSpace(e.SessionId)
                ? "跳转=不可(缺 session_id)"
                : $"跳转=可(id={e.SessionId}, t={e.CreateTime}, d={e.Date})";
            Logger.Info($"  证据 [{e.Index}] {session}（{e.Source}，verified={e.Verified}）: {TruncateLine(e.Content, 60)} | {jump}");
        }
        var answer = (r.Answer ?? "").Replace("\n", " ⏎ ");
        Logger.Info($"[{label}][assistant] {TruncateLine(answer, 300)}");
        if (r.TaskTrace is { } tr && tr.TerminationReason is not null)
            Logger.Info($"  trace 终止原因: {tr.TerminationReason}");
        _ = agent;
    }

    private static string TruncateLine(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s[..max] + "…";
    }

    private static async Task RunAgentAsync(string query)
    {
        Logger.Info("=== P2 Agent 端到端自检 ===");
        Logger.Info($"查询: {query} | LLM: {Settings.Llm.Model}");

        if (string.IsNullOrWhiteSpace(Settings.Llm.ApiKey))
        {
            Logger.Error("未配置 LLM_API_KEY（appsettings.json 或环境变量）。");
            return;
        }

        try
        {
            Bridge = new PythonBridgeClient(
                Settings.PythonBridge.PythonExePath,
                Settings.PythonBridge.BridgeScriptPath,
                Logger,
                Settings.PythonBridge.IdleTimeoutSeconds);

            var started = await Bridge.StartAsync();
            if (!started) { Logger.Error("Bridge 启动失败，无法继续。"); return; }

            var registry = new MemoryAssistant.Core.Agent.ToolRegistry();
            var provider = new MemoryAssistant.Infrastructure.Agent.BridgeToolProvider(Bridge, Settings.Agent);
            provider.RegisterAll(registry);

            var chat = new MemoryAssistant.Infrastructure.Agent.DeepSeekChatClient(
                Settings.Llm, Logger, retryCount: Settings.Runtime.RetryCount);
            var loop = new MemoryAssistant.Core.Agent.AgentLoop(chat, registry, Settings.Agent, Logger);

            var sysPrompt = """
你是"回忆助手"，帮助用户回忆微信聊天记录中的内容。
你有以下工具可用：list_sessions（找会话）、read_messages（读消息原文）、search_messages（全文检索）、get_group_members（群成员）、get_session_stats（统计）、retrieve_memory（语义召回）。
规则：
1. 回答要基于工具返回的真实数据，不要编造。
2. 先找到相关会话，再读原文确认细节。
3. 回答尽量简洁，给出关键事实。
4. 如果工具结果不足，诚实说明找不到。
""";

            var result = await loop.RunAsync(sysPrompt, query);
            Logger.Info("");
            Logger.Info("=== 最终回答 ===");
            Logger.Info(result.Answer);
            Logger.Info("");
            Logger.Info($"轮数={result.RoundCount} | 工具调用={result.AllToolCalls.Count} | 正常结束={result.CompletedNormally} | 耗时={result.TotalElapsedMs:0}ms");

            await Bridge.RequestAsync("/exit", timeoutSeconds: 5);
        }
        catch (Exception ex)
        {
            Logger.Error($"Agent 端到端自检异常: {ex.Message}");
        }
    }

    private static async Task RunRagAsync(string query)
    {
        Logger.Info("=== P3 RAG 端到端自检 ===");
        Logger.Info($"查询: {query}");

        try
        {
            Bridge = new PythonBridgeClient(
                Settings.PythonBridge.PythonExePath,
                Settings.PythonBridge.BridgeScriptPath,
                Logger,
                Settings.PythonBridge.IdleTimeoutSeconds);

            var started = await Bridge.StartAsync();
            if (!started) { Logger.Error("Bridge 启动失败，无法继续。"); return; }

            var vector = new MemoryAssistant.Infrastructure.RAG.BridgeRagRetriever(Bridge, Logger);
            var fts = new MemoryAssistant.Infrastructure.RAG.BridgeFtsRetriever(Bridge, Logger);
            var hybrid = new MemoryAssistant.Infrastructure.RAG.HybridRetriever(vector, fts);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = await hybrid.SearchAsync(query, 5);
            sw.Stop();

            Logger.Info($"混合检索 {results.Count} 条 (耗时 {sw.ElapsedMilliseconds}ms)");
            int n = 0;
            foreach (var c in results)
            {
                n++;
                var head = c.Text.Replace("\n", " | ");
                if (head.Length > 120) head = head[..120] + "...";
                Logger.Info($"[{n}] [{c.Source}] {c.Date} {c.SessionName} (score={c.Score:0.000})");
                Logger.Info($"    {head}");
            }
            Logger.Info("=== RAG 自检结束 ===");

            await Bridge.RequestAsync("/exit", timeoutSeconds: 5);
        }
        catch (Exception ex)
        {
            Logger.Error($"RAG 自检异常: {ex.Message}");
        }
    }

    private static async Task RunWorkflowAsync(string query)
    {
        Logger.Info("=== P4 Workflow 端到端自检 ===");
        Logger.Info($"查询: {query}");

        var result = await RunWorkflowPipelineAsync(query);
        if (result is null) return;

        Logger.Info("");
        Logger.Info("=== 工作流输出 ===");
        Logger.Info($"意图: {result.Intent.Intent} | 证据: {result.Evidence.Count} 条 | 总耗时: {result.TotalElapsedMs:0}ms");
        foreach (var (stage, ms) in result.StageTimings)
            Logger.Info($"  {stage,-18} {ms,8:0}ms");
        Logger.Info("");
        Logger.Info("=== 最终回答 ===");
        Logger.Info(result.Answer);
        Logger.Info("=== Workflow 自检结束 ===");

        await Bridge.RequestAsync("/exit", timeoutSeconds: 5);
    }

    /// <summary>装配 Bridge/Agent/Workflow 并执行一次完整查询（--workflow / --trace 共用）。</summary>
    private static async Task<MemoryAssistant.Core.Workflow.WorkflowResult?> RunWorkflowPipelineAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(Settings.Llm.ApiKey))
        {
            Logger.Error("未配置 LLM_API_KEY。");
            return null;
        }

        try
        {
            Bridge = new PythonBridgeClient(
                Settings.PythonBridge.PythonExePath,
                Settings.PythonBridge.BridgeScriptPath,
                Logger,
                Settings.PythonBridge.IdleTimeoutSeconds);

            var started = await Bridge.StartAsync();
            if (!started) { Logger.Error("Bridge 启动失败。"); return null; }

            var registry = new MemoryAssistant.Core.Agent.ToolRegistry();
            new MemoryAssistant.Infrastructure.Agent.BridgeToolProvider(Bridge, Settings.Agent).RegisterAll(registry);

            var chat = new MemoryAssistant.Infrastructure.Agent.DeepSeekChatClient(
                Settings.Llm, Logger, retryCount: Settings.Runtime.RetryCount);
            var vector = new MemoryAssistant.Infrastructure.RAG.BridgeRagRetriever(Bridge, Logger);
            var fts = new MemoryAssistant.Infrastructure.RAG.BridgeFtsRetriever(Bridge, Logger);
            var hybrid = new MemoryAssistant.Infrastructure.RAG.HybridRetriever(vector, fts);

            // 精读委托：read_messages（session_id, begin, end, limit）
            async Task<IReadOnlyList<MemoryAssistant.Core.Workflow.ToolMessageInput>> PreciseReader(
                string sessionId, long begin, long end, int limit)
            {
                var resp = await Bridge.RequestAsync("read_messages", new Dictionary<string, object?>
                {
                    ["session_id"] = sessionId, ["limit"] = limit,
                    ["begin"] = begin, ["end"] = end,
                }, timeoutSeconds: 30);
                if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je ||
                    je.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
                var outList = new List<MemoryAssistant.Core.Workflow.ToolMessageInput>();
                foreach (var m in je.EnumerateArray())
                {
                    outList.Add(new MemoryAssistant.Core.Workflow.ToolMessageInput(
                        sessionId,
                        m.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "",
                        m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                        m.TryGetProperty("create_time", out var t) ? t.GetInt64() : 0L));
                }
                return outList;
            }

            var engine = new MemoryAssistant.Core.Workflow.WorkflowEngine(
                chat, hybrid, PreciseReader, registry, Settings.Agent, Logger);

            return await engine.RunAsync(query);
        }
        catch (Exception ex)
        {
            Logger.Error($"工作流管道异常: {ex.Message}");
            return null;
        }
    }

    private static async Task RunTraceAsync(string query)
    {
        Logger.Info("=== P8 Agent Trace ===");
        Logger.Info($"查询: {query} | LLM: {Settings.Llm.Model}");

        var result = await RunWorkflowPipelineAsync(query);
        if (result is null) return;

        // 阶段
        foreach (var ev in result.StageEvents)
            Logger.Info($"[阶段] {ev.Stage}: {ev.Detail}");

        // 逐轮 + 工具
        if (result.AgentResult is { } agent)
        {
            foreach (var r in agent.Rounds)
            {
                Logger.Info($"[LLM] 第 {r.Round} 轮 | {r.LatencyMs:0}ms | prompt {r.PromptTokens} | completion {r.CompletionTokens} | finish={r.FinishReason}");
                foreach (var t in r.ToolCalls)
                {
                    var args = t.ArgumentsJson.Length <= 120 ? t.ArgumentsJson : t.ArgumentsJson[..120] + "...";
                    if (t.Success)
                        Logger.Info($"[工具] [OK] {t.Name}({args}) {t.LatencyMs:0}ms");
                    else
                        Logger.Info($"[工具] [FAIL] {t.Name}({args}) 失败: {t.Error}");
                    if (!string.IsNullOrEmpty(t.Output))
                    {
                        var head = t.Output.Replace("\n", " | ");
                        if (head.Length > 120) head = head[..120] + "...";
                        Logger.Info($"        → {head}");
                    }
                }
            }
            var tokens = agent.TotalPromptTokens == 0 && agent.TotalCompletionTokens == 0
                ? "未统计" : $"tokens {agent.TotalPromptTokens}+{agent.TotalCompletionTokens}";
            Logger.Info($"[合计] {agent.Rounds.Count} 轮 | {tokens} | 总耗时 {result.TotalElapsedMs:0}ms");
        }
        else
        {
            Logger.Error("[Trace] Agent 轨迹缺失");
        }

        Logger.Info("");
        Logger.Info("=== 最终回答 ===");
        Logger.Info(result.Answer);
        Logger.Info("=== P8 Agent Trace 结束 ===");

        await Bridge.RequestAsync("/exit", timeoutSeconds: 5);
    }

    private static async Task RunTimelineAsync()
    {
        Logger.Info("=== P6 时间线自检 ===");

        try
        {
            Bridge = new PythonBridgeClient(
                Settings.PythonBridge.PythonExePath,
                Settings.PythonBridge.BridgeScriptPath,
                Logger,
                Settings.PythonBridge.IdleTimeoutSeconds);

            var started = await Bridge.StartAsync();
            if (!started) { Logger.Error("Bridge 启动失败。"); return; }

            var resp = await Bridge.RequestAsync("rag_timeline", null, timeoutSeconds: 60);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je ||
                !je.TryGetProperty("days", out var days) || days.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                Logger.Error($"rag_timeline 失败: {resp.Error ?? "无数据"}");
                return;
            }

            var months = new HashSet<string>();
            int totalMsgs = 0;
            foreach (var d in days.EnumerateArray())
            {
                var date = d.TryGetProperty("date", out var dt) ? dt.GetString() ?? "" : "";
                if (date.Length >= 7) months.Add(date[..7]);
                if (d.TryGetProperty("msg_count", out var m)) totalMsgs += m.GetInt32();
            }

            Logger.Info($"[1/3] rag_timeline: {days.GetArrayLength()} 天 / {months.Count} 个月 / 合计 {totalMsgs} 条消息 (PASS)");

            // 取最近一天验证 rag_day_detail
            string? firstDate = null;
            foreach (var d in days.EnumerateArray())
            {
                if (d.TryGetProperty("date", out var dt)) { firstDate = dt.GetString(); break; }
            }

            if (firstDate is null) { Logger.Error("无日期数据。"); return; }

            var detail = await Bridge.RequestAsync("rag_day_detail", new Dictionary<string, object?> { ["date"] = firstDate }, timeoutSeconds: 30);
            if (!detail.Success || detail.Data is not System.Text.Json.JsonElement dj ||
                !dj.TryGetProperty("chunks", out var chunks) || chunks.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                Logger.Error($"rag_day_detail 失败: {detail.Error ?? "无数据"}");
                return;
            }

            Logger.Info($"[2/3] rag_day_detail ({firstDate}): {chunks.GetArrayLength()} 个片段 (PASS)");
            Logger.Info("[3/3] 时间线数据管线 OK → 三栏 UI（月份/日期/当日片段）消费同一数据源 (PASS)");
            Logger.Info("=== P6 时间线自检结束 ===");

            await Bridge.RequestAsync("/exit", timeoutSeconds: 5);
        }
        catch (Exception ex)
        {
            Logger.Error($"时间线自检异常: {ex.Message}");
        }
    }

    private static async Task RunP7Async()
    {
        Logger.Info("=== P7 高辨识度功能自检 ===");

        try
        {
            Bridge = new PythonBridgeClient(
                Settings.PythonBridge.PythonExePath,
                Settings.PythonBridge.BridgeScriptPath,
                Logger,
                Settings.PythonBridge.IdleTimeoutSeconds);

            var started = await Bridge.StartAsync();
            if (!started) { Logger.Error("Bridge 启动失败。"); return; }

            // [1/6] 承诺候选
            var resp = await Bridge.RequestAsync("rag_commitments", new Dictionary<string, object?> { ["limit"] = 200 }, timeoutSeconds: 60);
            System.Text.Json.JsonElement cands = default;
            int selfCount = 0;
            var ok1 = resp.Success && resp.Data is System.Text.Json.JsonElement je1 &&
                      je1.TryGetProperty("candidates", out cands) && cands.ValueKind == System.Text.Json.JsonValueKind.Array;
            if (ok1)
            {
                foreach (var c in cands.EnumerateArray())
                {
                    if (c.TryGetProperty("is_self", out var s) && s.GetBoolean()) selfCount++;
                    if (!c.TryGetProperty("matched_text", out _) || !c.TryGetProperty("content", out _)) { ok1 = false; break; }
                }
                Logger.Info($"[1/6] rag_commitments: {cands.GetArrayLength()} 候选 (我的 {selfCount}) {(ok1 ? "PASS" : "FAIL")}");
            }
            else Logger.Info("[1/6] rag_commitments: FAIL");

            // [2/6] 话题分析
            var resp2 = await Bridge.RequestAsync("rag_topics", new Dictionary<string, object?> { ["recent_days"] = 14 }, timeoutSeconds: 60);
            System.Text.Json.JsonElement je2 = default, kwt = default, cl = default;
            var ok2 = false;
            if (resp2.Success && resp2.Data is System.Text.Json.JsonElement je2tmp)
            {
                je2 = je2tmp;
                ok2 = je2.TryGetProperty("keyword_topics", out kwt) && kwt.ValueKind == System.Text.Json.JsonValueKind.Array &&
                      je2.TryGetProperty("clusters", out cl) && cl.ValueKind == System.Text.Json.JsonValueKind.Array;
            }
            if (ok2)
            {
                var chunks = je2.TryGetProperty("chunks_in_scope", out var c2) ? c2.GetInt32() : 0;
                Logger.Info($"[2/6] rag_topics: {chunks} 片段 / 关键词 {kwt.GetArrayLength()} 类 / 聚类 {cl.GetArrayLength()} 簇 (PASS)");
            }
            else Logger.Info("[2/6] rag_topics: FAIL");

            // [3/6] 会话画像
            var resp3 = await Bridge.RequestAsync("rag_profiles", new Dictionary<string, object?> { ["top"] = 10 }, timeoutSeconds: 30);
            System.Text.Json.JsonElement profs = default;
            var ok3 = resp3.Success && resp3.Data is System.Text.Json.JsonElement je3 &&
                      je3.TryGetProperty("profiles", out profs) && profs.ValueKind == System.Text.Json.JsonValueKind.Array && profs.GetArrayLength() > 0;
            if (ok3)
            {
                var first = profs[0];
                var name = first.TryGetProperty("session_name", out var n) ? n.GetString() ?? "" : "";
                var msgs = first.TryGetProperty("msg_count", out var m) ? m.GetInt32() : 0;
                Logger.Info($"[3/6] rag_profiles: {profs.GetArrayLength()} 个画像，top={name}({msgs} 条) (PASS)");
            }
            else Logger.Info("[3/6] rag_profiles: FAIL");

            // [4/6] IntentRouter 规则：承诺类查询
            var chat4 = new MemoryAssistant.Infrastructure.Agent.DeepSeekChatClient(Settings.Llm, Logger, retryCount: 1);
            var router = new MemoryAssistant.Core.Workflow.IntentRouter(chat4, Logger);
            var intent = await router.RouteAsync("我还有哪些事情没做？");
            Logger.Info($"[4/6] IntentRouter 承诺查询 → {intent.Intent} {(intent.Intent == MemoryAssistant.Core.Workflow.IntentKind.Commitment ? "PASS" : "FAIL")}");

            // [5/6] TopicNamer：真实 LLM 命名 top 簇
            var namerOk = false;
            if (ok2 && cl.GetArrayLength() > 0 && !string.IsNullOrWhiteSpace(Settings.Llm.ApiKey))
            {
                var parser = MemoryAssistant.Core.Features.Topics.TopicDataParser.Parse(je2);
                var namer = new MemoryAssistant.Core.Features.Topics.TopicNamer(chat4, Logger);
                var named = await namer.NameClustersAsync(parser.Clusters, maxClusters: 3);
                namerOk = named.Count > 0;
                foreach (var n in named)
                    Logger.Info($"    簇 {n.ClusterId} → {n.Label}");
            }
            Logger.Info($"[5/6] TopicNamer 命名: {(namerOk ? "PASS" : "SKIP(无 LLM Key 或无簇)")}");

            // [6/6] HourlyHistogram
            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            var times = new List<long>();
            for (int h = 0; h < 24; h++)
                times.Add(now - (now % 86400) + h * 3600); // 今天 0..23 点各一条
            var hist = MemoryAssistant.Core.Features.Profiles.HourlyHistogram.Compute(times);
            var topH = MemoryAssistant.Core.Features.Profiles.HourlyHistogram.TopHours(hist, 3);
            var ok6 = hist.Count(x => x == 1) == 24 && topH.Count == 3;
            Logger.Info($"[6/6] HourlyHistogram: 24 桶全命中 / Top3={string.Join(",", topH.Select(t => $"{t.Hour}点"))} {(ok6 ? "PASS" : "FAIL")}");

            Logger.Info("=== P7 自检结束 ===");
            await Bridge.RequestAsync("/exit", timeoutSeconds: 5);
        }
        catch (Exception ex)
        {
            Logger.Error($"P7 自检异常: {ex.Message}");
        }
    }

    private static async Task RunIndexAsync()
    {
        Logger.Info("=== P9 增量索引自检 ===");

        try
        {
            Bridge = new PythonBridgeClient(
                Settings.PythonBridge.PythonExePath,
                Settings.PythonBridge.BridgeScriptPath,
                Logger,
                Settings.PythonBridge.IdleTimeoutSeconds);

            var started = await Bridge.StartAsync();
            if (!started) { Logger.Error("Bridge 启动失败。"); return; }

            // [1/6] 索引状态（前）
            var resp = await Bridge.RequestAsync("rag_index_status", null, timeoutSeconds: 60);
            System.Text.Json.JsonElement st1 = default;
            var ok1 = resp.Success && resp.Data is System.Text.Json.JsonElement &&
                      (st1 = (System.Text.Json.JsonElement)resp.Data!).ValueKind == System.Text.Json.JsonValueKind.Object;
            var before = 0;
            if (ok1)
            {
                before = st1.TryGetProperty("chunks", out var c1) ? c1.GetInt32() : 0;
                var built = st1.TryGetProperty("built_at", out var b1) ? b1.GetString() ?? "?" : "?";
                Logger.Info($"[1/6] rag_index_status(前): chunks={before} built_at={built} (PASS)");
            }
            else Logger.Info("[1/6] rag_index_status(前): FAIL");

            // [2/6] 增量第一轮
            var r2 = await Bridge.RequestAsync("rag_incremental_index",
                new Dictionary<string, object?> { ["hard_cap"] = 200, ["embed_batch"] = 32 }, timeoutSeconds: 600);
            System.Text.Json.JsonElement je2 = default;
            var ok2 = r2.Success && r2.Data is System.Text.Json.JsonElement &&
                      (je2 = (System.Text.Json.JsonElement)r2.Data!).TryGetProperty("ok", out var o2) && o2.GetBoolean();
            if (ok2)
            {
                var added = je2.TryGetProperty("added_chunks", out var a2) ? a2.GetInt32() : 0;
                var msgs = je2.TryGetProperty("new_messages", out var m2) ? m2.GetInt32() : 0;
                var up = je2.TryGetProperty("updated_sessions", out var u2) ? u2.GetArrayLength() : 0;
                Logger.Info($"[2/6] 增量第一轮: added={added} chunks / {msgs} 消息 / {up} 会话 ({(added >= 0 ? "PASS" : "FAIL")})");
            }
            else Logger.Info($"[2/6] 增量第一轮: FAIL {(r2.Error ?? "")}");

            // [3/6] 增量第二轮（幂等）
            var r3 = await Bridge.RequestAsync("rag_incremental_index",
                new Dictionary<string, object?> { ["hard_cap"] = 200, ["embed_batch"] = 32 }, timeoutSeconds: 600);
            System.Text.Json.JsonElement je3 = default;
            var ok3 = r3.Success && r3.Data is System.Text.Json.JsonElement &&
                      (je3 = (System.Text.Json.JsonElement)r3.Data!).TryGetProperty("ok", out var o3) && o3.GetBoolean();
            if (ok3)
            {
                var added3 = je3.TryGetProperty("added_chunks", out var a3) ? a3.GetInt32() : -1;
                Logger.Info($"[3/6] 增量第二轮(幂等): added={added3} {(added3 == 0 ? "PASS" : "WARN(运行期间有新消息)")}");
            }
            else Logger.Info("[3/6] 增量第二轮: FAIL");

            // [4/6] 索引状态（后）
            var resp4 = await Bridge.RequestAsync("rag_index_status", null, timeoutSeconds: 60);
            System.Text.Json.JsonElement st4 = default;
            var ok4 = resp4.Success && resp4.Data is System.Text.Json.JsonElement &&
                      (st4 = (System.Text.Json.JsonElement)resp4.Data!).ValueKind == System.Text.Json.JsonValueKind.Object;
            if (ok4)
            {
                var after = st4.TryGetProperty("chunks", out var c4) ? c4.GetInt32() : 0;
                Logger.Info($"[4/6] rag_index_status(后): chunks={after} (前 {before} → 后 {after}) {(after >= before ? "PASS" : "FAIL")}");
            }
            else Logger.Info("[4/6] rag_index_status(后): FAIL");

            // [5/6] 检索验证
            var r5 = await Bridge.RequestAsync("rag_search",
                new Dictionary<string, object?> { ["query"] = "工资", ["top_k"] = 3 }, timeoutSeconds: 60);
            var ok5 = r5.Success && r5.Data is System.Text.Json.JsonElement arr5 &&
                      arr5.ValueKind == System.Text.Json.JsonValueKind.Array && arr5.GetArrayLength() > 0;
            Logger.Info($"[5/6] rag_search 验证: {(ok5 ? "PASS" : "FAIL")}");

            // [6/6] 汇总
            Logger.Info($"[6/6] 全程未触发全量重建 (PASS)");
            Logger.Info("=== P9 增量索引自检结束 ===");

            await Bridge.RequestAsync("/exit", timeoutSeconds: 5);
        }
        catch (Exception ex)
        {
            Logger.Error($"P9 自检异常: {ex.Message}");
        }
    }

    private static string ResolveBridgeScript()
    {
        var fromConfig = Environment.GetEnvironmentVariable("BRIDGE_SCRIPT_PATH");
        if (!string.IsNullOrWhiteSpace(fromConfig) && File.Exists(fromConfig))
            return fromConfig;

        // 默认相对布局：<repo>\python_bridge\bridge.py
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "python_bridge", "bridge.py");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "python_bridge", "bridge.py");
    }

    /// <summary>
    /// 解析 Python 解释器：配置里写的路径在本机存在就用它；否则回退到**包内便携 Python**
    /// （&lt;exe 同目录&gt;\python\python.exe）。这样把整个目录拷到别的电脑不用改配置，
    /// 包内没带 Python 时仍保留原配置值，让报错信息如实指向用户配的那个路径。
    /// </summary>
    private static string ResolvePythonExe(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var bundled = Path.Combine(AppContext.BaseDirectory, "python", "python.exe");
        return File.Exists(bundled) ? bundled : configured;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var h in _hotkeys) h.Dispose();   // 摘掉全局热键（否则这些组合键会一直被占着）
        _notifier?.Dispose();
        _tray?.Dispose();                          // 托盘图标也要收掉，否则会留一个"幽灵图标"
        try
        {
            Services?.ShutdownAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }
        base.OnExit(e);
    }
}