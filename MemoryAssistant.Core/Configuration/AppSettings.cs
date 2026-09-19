namespace MemoryAssistant.Core.Configuration;

/// <summary>LLM 配置。</summary>
public sealed class LlmOptions
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string ApiKey { get; set; } = "";
    /// <summary>
    /// 模型名（默认 deepseek-flash：实测同时支持 Function Calling 与 reasoning_content，
    /// 比 deepseek-chat / deepseek-reasoner 更适合本项目的"思考 + 调工具"组合；
    /// 注意 /models 现在只列 deepseek-flash 与 deepseek-v4-pro，旧两个已不在列）。
    /// </summary>
    public string Model { get; set; } = "deepseek-flash";
    public double Temperature { get; set; } = 0.2;
}

/// <summary>Embedding 配置。</summary>
public sealed class EmbeddingOptions
{
    /// <summary>local | api。</summary>
    public string Backend { get; set; } = "local";
    public string Model { get; set; } = "BAAI/bge-small-zh-v1.5";
    public string ApiKey { get; set; } = "";
}

/// <summary>RAG 配置。</summary>
public sealed class RagOptions
{
    public int TopK { get; set; } = 5;
    public int ChunkSize { get; set; } = 200;
}

/// <summary>
/// Agent 配置（plan2 §16：所有预算可配置，不散落为代码常量）。
/// MaxPlanSteps/MaxRepeatedToolCalls 供 P12 Planner/P15 Evaluator 阶段消费。
/// </summary>
public sealed class AgentOptions
{
    public int MaxRounds { get; set; } = 6;
    public int MaxToolResultChars { get; set; } = 4000;
    public int MaxPlanSteps { get; set; } = 8;
    public int MaxToolCalls { get; set; } = 20;
    public int MaxRepeatedToolCalls { get; set; } = 3;
    public int MaxFailures { get; set; } = 3;
    public int MaxTaskSeconds { get; set; } = 120;
    public bool EnableReplanning { get; set; } = true;
    /// <summary>最大重规划轮数（Evaluator 判不足后换策略的次数上限，plan2 §7）。</summary>
    public int MaxReplanRounds { get; set; } = 3;
    public bool EnableTrace { get; set; } = false;
    /// <summary>
    /// 后台补齐新聊天的间隔（分钟，0 = 关闭）。索引只能覆盖"建索引那一刻"的数据，
    /// 不常补的话"翻最新的记录"永远差几天——默认 5 分钟，后台像一直盯着一样。
    /// </summary>
    public int IndexRefreshMinutes { get; set; } = 5;
    /// <summary>
    /// 是否用模型把证据组织成自然语言回答（默认开）。
    /// 关闭后回答退化为确定性的"原始记录摘录"，用于离线/省 token 场景。
    /// </summary>
    public bool EnableAnswerSynthesis { get; set; } = true;

    /// <summary>
    /// 是否让模型（LLM Planner）自己分析意图并决定"该做什么"（默认开）。
    /// 打开后每次提问多一次规划调用：模型先想清楚要查什么/用哪个能力，再交给执行；
    /// LLM 不可用或计划非法时自动回退规则规划（RulePlanner），不会因此不可用。
    /// </summary>
    public bool EnableLlmPlanning { get; set; } = true;

    /// <summary>
    /// 自动回复的全局刹车：true = 一律不自动发送（只生成草稿写进任务结果）。默认 false（按任务配置执行）。
    ///
    /// 为什么叫"刹车"而不是"开关"：配置合并对 bool 的缺省值不友好——若默认 true，
    /// 旧配置文件里没有这个键就会被反序列化成 false，反而把功能静默关掉。
    /// 真正决定"这个任务会不会自动发消息"的是任务自身的 Action=AutoReply（创建时用户显式确认过）。
    /// </summary>
    public bool SuppressAutoReply { get; set; } = false;

    /// <summary>
    /// 发送对象白名单（第三阶段补充，**默认空 = 不启用**）。
    ///
    /// 一旦填了名字，就**只有名单内的对象能被发送**，其余一律拒绝执行（连确认弹窗都不弹）——
    /// 这是"绝不许发错人"的最后一道硬闸，而不是靠用户每次手点确认来兜。
    /// 判定用的是**会话目录核对后的真名**（备注名/昵称/群名都算，忽略大小写与首尾空格），
    /// 所以把"周斌"写进名单后，"给周斌发消息"才会放行，点到"周斌的工作群"会被拦住。
    /// </summary>
    public List<string> SendAllowList { get; set; } = [];

    /// <summary>
    /// 是否用**专用通知智能体**播报长期任务的执行结果（默认开）。
    ///
    /// 打开后：后台任务跑完的新记录先由它裁决"值不值得打断用户"，值得才弹一条系统通知
    /// （"这次没动静"那种不会被播报，见 MissionNotificationAgent 的过滤口径）。
    /// 关掉就完全不打扰，只写执行账本与任务页。
    /// </summary>
    public bool NotifyMissionRuns { get; set; } = true;

    // ---- 数据节流（EcoMode）：验收/自检时只读部分会话的小样本，省 token ----
    /// <summary>验收模式下自动置 true；正常 GUI 启动默认 false（全量）。--full-data 可显式关闭。</summary>
    public bool EcoMode { get; set; } = false;
    /// <summary>Eco：read_messages / search_messages 的 limit 上限（单次读条数）。</summary>
    public int EcoMaxReadLimit { get; set; } = 15;
    /// <summary>Eco：list_sessions 的 limit 上限（列出会话数）。</summary>
    public int EcoMaxSessions { get; set; } = 8;
    /// <summary>Eco：get_session_stats 的 session_limit 上限（无 session_id 时扫几个会话）。</summary>
    public int EcoMaxSessionLimit { get; set; } = 12;
    /// <summary>Eco：retrieve_memory 的 top_k 上限。</summary>
    public int EcoMaxTopK { get; set; } = 4;

    /// <summary>
    /// 任务调度器的巡检 tick（秒，V4.1）。任务的实际节奏由各自的 IntervalMinutes/IntervalSeconds 决定，
    /// 这个值只决定"最多晚多久被发现到期"——秒级任务（每 30 秒）需要它足够小。
    /// 原来硬编码 10 秒，现在可配置（调大能省一点空转，调小让秒级任务更准）。
    /// </summary>
    public int MissionTickSeconds { get; set; } = 10;
}

/// <summary>
/// 界面行为配置（V4.1）。
/// </summary>
public sealed class UiOptions
{
    /// <summary>
    /// 页面"自动重拉"的最小间隔（秒）；<c>0</c> = 关闭自动刷新（只在手动点「刷新」时加载）。
    ///
    /// 为什么要有它：时间线/画像/承诺/话题这些页面原来**每次切到该 Tab 都无条件重拉**
    /// （话题页还会跑一次 LLM 聚类），来回切几次就重复打接口、白耗额度与算力；
    /// 改成按这个间隔节流，并给每个页面一个手动「刷新」兜底。
    /// </summary>
    public int AutoRefreshSeconds { get; set; } = 60;
}

/// <summary>WxChat SDK 数据源配置。</summary>
public sealed class WxChatOptions
{
    public string DataDir { get; set; } = "";
    public string SnapshotDir { get; set; } = "";
}

/// <summary>Python Bridge 配置。</summary>
public sealed class PythonBridgeOptions
{
    public string PythonExePath { get; set; } = "";
    public string BridgeScriptPath { get; set; } = "";
    public int IdleTimeoutSeconds { get; set; } = 30;
}

/// <summary>运行时通用配置。</summary>
public sealed class RuntimeOptions
{
    public int TimeoutSeconds { get; set; } = 60;
    public int RetryCount { get; set; } = 2;
}

/// <summary>应用全局配置集合。</summary>
public sealed class AppSettings
{
    public LlmOptions Llm { get; set; } = new();
    public EmbeddingOptions Embedding { get; set; } = new();
    public RagOptions Rag { get; set; } = new();
    public AgentOptions Agent { get; set; } = new();
    public UiOptions Ui { get; set; } = new();
    public WxChatOptions WxChat { get; set; } = new();
    public PythonBridgeOptions PythonBridge { get; set; } = new();
    public RuntimeOptions Runtime { get; set; } = new();
}