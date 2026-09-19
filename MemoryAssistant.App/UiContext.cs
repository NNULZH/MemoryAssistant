using MemoryAssistant.Core.Environment;

namespace MemoryAssistant.App;

/// <summary>
/// 界面状态发布器（Core <see cref="IUiContext"/> 的 WPF 实现）。
///
/// 界面各处（窗口、任务页、确认弹窗）把"用户现在在哪、选中了什么"写进来，
/// Agent 通过 get_ui_context / get_environment_context 读走——
/// 这样"把刚才那个任务暂停"才定位得到"刚才那个"到底是谁。
///
/// 做成进程内单例：能改这些状态的组件散落在窗口、页面、对话框里，
/// 一路注入会把参数传得到处都是；而它本身就是"当前界面"这一个事实。
/// </summary>
public sealed class UiContext : IUiContext
{
    public static UiContext Current { get; } = new();

    private UiContext() { }

    private string _page = "首页";
    private string _session = "";
    private string _mission = "";
    private string _dialog = "";
    private string _pending = "";

    public string CurrentPage => string.IsNullOrWhiteSpace(_page) ? "（未知）" : _page;
    public string SelectedSession => _session;
    public string SelectedMission => _mission;
    public string OpenDialog => _dialog;
    public string PendingConfirmation => _pending;

    /// <summary>切页时调用（MainWindow 导航处）。</summary>
    public void SetPage(string page) => _page = page ?? "";

    /// <summary>打开/关闭会话查看窗口时调用。</summary>
    public void SetSession(string? session) => _session = session ?? "";

    /// <summary>任务页选中项变化时调用（离开任务页传空）。</summary>
    public void SetMission(string? mission) => _mission = mission ?? "";

    /// <summary>打开/关闭任意对话框时调用（确认弹窗、聊天记录窗口…）。</summary>
    public void SetDialog(string? dialog) => _dialog = dialog ?? "";

    /// <summary>等待用户确认的动作描述（弹确认框期间）。</summary>
    public void SetPendingConfirmation(string? description) => _pending = description ?? "";
}
