using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.Core.RAG;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.ViewModels;

/// <summary>设置页 ViewModel：读取/保存 LLM 与 Embedding 配置 + 索引管理（P9）。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// <summary>构造期间回填字段会触发属性变更，不该被当成"用户改动了"。</summary>
    private bool _loading = true;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        ApiKey = services.Settings.Llm.ApiKey;
        Model = services.Settings.Llm.Model;
        BaseUrl = services.Settings.Llm.BaseUrl;
        EmbedBackend = services.Settings.Embedding.Backend;
        TopK = services.Settings.Rag.TopK;
        AutoRefreshSeconds = services.Settings.Ui.AutoRefreshSeconds.ToString();
        IndexRefreshMinutes = services.Settings.Agent.IndexRefreshMinutes.ToString();
        _loading = false;
    }

    /// <summary>
    /// 页面上有改动、还没点「保存」。
    /// 设置页是**显式保存**语义（不自动落盘），所以必须把"还没保存"显示出来，
    /// 否则用户会以为改完就已经生效了（上次就是这么被误导的）。
    /// </summary>
    [ObservableProperty]
    private bool _hasUnsavedChanges;

    // ---- 任何一处改动都点亮"未保存"标记 ----
    partial void OnApiKeyChanged(string value) => MarkDirty();
    partial void OnModelChanged(string value) => MarkDirty();
    partial void OnBaseUrlChanged(string value) => MarkDirty();
    partial void OnEmbedBackendChanged(string value) => MarkDirty();
    partial void OnTopKChanged(int value) => MarkDirty();
    partial void OnAutoRefreshSecondsChanged(string value) => MarkDirty();
    partial void OnIndexRefreshMinutesChanged(string value) => MarkDirty();

    private void MarkDirty()
    {
        if (_loading) return;
        HasUnsavedChanges = true;
    }

    /// <summary>
    /// 页面自动刷新间隔（秒，V4.1）；<c>0</c> = 关闭自动刷新（只在手动点「刷新」时加载）。
    /// 与 <see cref="IndexRefreshMinutes"/> 一起构成"数据刷新别那么快"的两个旋钮。
    /// </summary>
    [ObservableProperty]
    private string _autoRefreshSeconds = "60";

    /// <summary>后台 RAG 增量索引的间隔（分钟，V4.1；0 = 关闭）。</summary>
    [ObservableProperty]
    private string _indexRefreshMinutes = "5";

    [ObservableProperty]
    private string _apiKey = "";

    [ObservableProperty]
    private string _model = "";

    [ObservableProperty]
    private string _baseUrl = "";

    [ObservableProperty]
    private string _embedBackend = "";

    [ObservableProperty]
    private int _topK;

    [ObservableProperty]
    private string _statusText = "";

    // ---- P9 索引管理 ----

    [ObservableProperty]
    private string _indexStatusText = "未加载（点击“刷新状态”）";

    [ObservableProperty]
    private bool _isIndexBusy;

    [ObservableProperty]
    private string _rebuildButtonText = "全量重建";

    public ObservableCollection<string> IndexLogs { get; } = [];

    private bool _rebuildArmed;

    /// <summary>
    /// 保存本机配置到 appsettings.local.json（已 gitignore）。
    /// 覆盖页面上**所有可编辑字段**（曾经只存 apiKey + 两个间隔，模型/Base URL/TopK 改了等于没改，
    /// 用户以为"没有保存"就是这么来的）。
    ///
    /// 生效时机：聊天客户端持有的是同一个 LlmOptions 对象引用，且每次请求都重读
    /// BaseUrl/ApiKey/Model，所以这三项**立即生效**；索引补齐间隔立即重起后台表；
    /// 页面自动刷新间隔是建页面时读取的，重新进入页面才生效。
    /// TopK 与 Embedding 后端当前由 Python 侧/RAG 模块决定，这里仅落盘备查——不谎称已生效。
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var localPath = Path.Combine(appDir, "appsettings.local.json");

            var apiKey = ApiKey.Trim();
            var model = Model.Trim();
            var baseUrl = BaseUrl.Trim();
            var embedBackend = EmbedBackend.Trim();
            var topK = TopK > 0 ? TopK : 5;
            var autoRefresh = ParseNonNegative(AutoRefreshSeconds, 60);
            var indexRefresh = ParseNonNegative(IndexRefreshMinutes, 5);

            var payload = new Dictionary<string, object>
            {
                ["llm"] = new Dictionary<string, object?>
                {
                    ["apiKey"] = apiKey,
                    ["model"] = model,
                    ["baseUrl"] = baseUrl,
                },
                ["embedding"] = new Dictionary<string, object?> { ["backend"] = embedBackend },
                ["rag"] = new Dictionary<string, object?> { ["topK"] = topK },
                ["ui"] = new Dictionary<string, object?> { ["autoRefreshSeconds"] = autoRefresh },
                ["agent"] = new Dictionary<string, object?> { ["indexRefreshMinutes"] = indexRefresh },
            };
            File.WriteAllText(localPath, System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            _services.Settings.Llm.ApiKey = apiKey;
            if (model.Length > 0) _services.Settings.Llm.Model = model;          // 空值不覆盖：不把模型名写成空串
            if (baseUrl.Length > 0) _services.Settings.Llm.BaseUrl = baseUrl;
            if (embedBackend.Length > 0) _services.Settings.Embedding.Backend = embedBackend;
            _services.Settings.Rag.TopK = topK;
            _services.Settings.Ui.AutoRefreshSeconds = autoRefresh;
            _services.Settings.Agent.IndexRefreshMinutes = indexRefresh;
            _services.StartBackgroundIndexRefresh();   // 新间隔立即生效（不再等下次启动）

            HasUnsavedChanges = false;
            StatusText = $"已保存到 {localPath}（仅本机，不入 git）。"
                       + "API Key / 模型 / Base URL 立即生效；索引补齐间隔已重启后台表；"
                       + "页面自动刷新间隔在重新进入相关页面后生效。"
                       + "TopK 与 Embedding 后端由 Python 侧决定，这里仅落盘备查。";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>解析输入框里的非负整数；非法就回退默认值（不让一次手滑写坏配置）。</summary>
    private static int ParseNonNegative(string? raw, int fallback)
        => int.TryParse((raw ?? "").Trim(), out var v) && v >= 0 ? v : fallback;

    [RelayCommand]
    private async Task RefreshIndexStatusAsync()
    {
        IndexLogs.Clear();
        IsIndexBusy = true;
        IndexStatusText = "读取索引状态...";
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_index_status", null, timeoutSeconds: 60);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je)
            {
                IndexStatusText = $"读取失败：{resp.Error ?? "无数据"}";
                return;
            }
            var s = IndexStatusParser.Parse(je);
            if (s.Error is not null)
            {
                IndexStatusText = $"索引错误：{s.Error}";
                return;
            }
            IndexStatusText = s.Loaded
                ? $"chunks={s.Chunks} | 会话={s.Sessions} | 消息={s.TotalMsgs} | 维度={s.Dim} | 构建={s.BuiltAt ?? "?"}"
                : $"索引未加载（chunks={s.Chunks}）";
        }
        catch (Exception ex)
        {
            IndexStatusText = $"读取异常：{ex.Message}";
        }
        finally
        {
            IsIndexBusy = false;
        }
    }

    [RelayCommand]
    private async Task IncrementalIndexAsync()
    {
        IndexLogs.Clear();
        IsIndexBusy = true;
        IndexStatusText = "增量更新中（只处理新消息，通常数秒）...";
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_incremental_index",
                new Dictionary<string, object?> { ["hard_cap"] = 200, ["embed_batch"] = 32, ["sample_limit"] = 2000 },
                timeoutSeconds: 600);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je)
            {
                IndexStatusText = $"增量更新失败：{resp.Error ?? "无数据"}";
                return;
            }
            foreach (var l in ReadLogs(je)) IndexLogs.Add(l);
            var ok = je.TryGetProperty("ok", out var o) && o.GetBoolean();
            if (!ok)
            {
                IndexStatusText = $"增量更新失败：{GetStr(je, "error")}";
                return;
            }
            var added = GetInt(je, "added_chunks");
            var msgs = GetInt(je, "new_messages");
            var elapsed = GetNum(je, "elapsed_sec");
            IndexStatusText = added == 0
                ? $"增量更新完成：无新消息（幂等） | {elapsed}s"
                : $"增量更新完成：新增 {added} chunks / {msgs} 条消息 | {elapsed}s（注：Bridge 阻塞式，日志完成后一次性显示）";
        }
        catch (Exception ex)
        {
            IndexStatusText = $"增量更新异常：{ex.Message}";
        }
        finally
        {
            IsIndexBusy = false;
        }
    }

    [RelayCommand]
    private async Task RebuildIndexAsync()
    {
        if (!_rebuildArmed)
        {
            _rebuildArmed = true;
            RebuildButtonText = "再次点击确认全量重建";
            IndexStatusText = "确认后再点一次（全量重建约 7 分钟）";
            return;
        }
        _rebuildArmed = false;
        RebuildButtonText = "全量重建";
        IndexLogs.Clear();
        IsIndexBusy = true;
        IndexStatusText = "全量重建中（约 7 分钟）...";
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_build_index",
                new Dictionary<string, object?> { ["scope"] = "all", ["recent_days"] = 0 },
                timeoutSeconds: 900);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je)
            {
                IndexStatusText = $"全量重建失败：{resp.Error ?? "无数据"}";
                return;
            }
            foreach (var l in ReadLogs(je)) IndexLogs.Add(l);
            var ok = je.TryGetProperty("ok", out var o) && o.GetBoolean();
            IndexStatusText = ok
                ? $"全量重建完成：{GetInt(je, "chunks")} chunks / {GetInt(je, "sessions")} 会话 / {GetNum(je, "elapsed_sec")}s"
                : $"全量重建失败：{GetStr(je, "error")}";
        }
        catch (Exception ex)
        {
            IndexStatusText = $"全量重建异常：{ex.Message}";
        }
        finally
        {
            IsIndexBusy = false;
        }
    }

    private static IEnumerable<string> ReadLogs(System.Text.Json.JsonElement je)
    {
        if (je.TryGetProperty("progress_logs", out var logs) && logs.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var l in logs.EnumerateArray())
            {
                if (l.ValueKind == System.Text.Json.JsonValueKind.String)
                    yield return l.GetString() ?? "";
            }
        }
    }

    private static string GetStr(System.Text.Json.JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int GetInt(System.Text.Json.JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : 0;

    private static double GetNum(System.Text.Json.JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : 0;
}
