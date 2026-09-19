using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.Core.RAG;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.ViewModels;

/// <summary>知识库里召回的一条记忆（UI 卡片）。</summary>
public sealed record MemoryCardItem(
    int Rank,
    string SessionName,
    string Date,
    string Preview,
    string ScoreText,
    string SourceText,
    string SizeText);

/// <summary>
/// 知识库页 ViewModel（个人聊天记忆 = Agent 的长期记忆）。
///
/// 这一页不重新实现 RAG：索引状态读 Bridge，召回用既有的 HybridRetriever（向量 + 全文）。
/// 目的只有一个——让"Agent 确实拥有一个可检索的知识库"这件事在界面上看得见：
/// 有多少记忆、什么时候建的、问一句能召回哪几条、来自哪个会话、相关度多少。
/// </summary>
public partial class KnowledgeViewModel : ObservableObject
{
    private readonly AppServices _services;

    public KnowledgeViewModel(AppServices services)
    {
        _services = services;
        TopK = services.Settings.Rag.TopK > 0 ? services.Settings.Rag.TopK : 5;
    }

    /// <summary>召回条数（沿用配置里的 TopK）。</summary>
    public int TopK { get; }

    // ---- 索引状态 ----

    [ObservableProperty]
    private string _chunkCountText = "—";

    [ObservableProperty]
    private string _sessionCountText = "—";

    [ObservableProperty]
    private string _messageCountText = "—";

    [ObservableProperty]
    private string _dimText = "—";

    [ObservableProperty]
    private string _builtAtText = "—";

    [ObservableProperty]
    private string _indexNote = "点「刷新状态」读取本地索引。";

    [ObservableProperty]
    private bool _isIndexBusy;

    [ObservableProperty]
    private string _actionStatus = "";

    [ObservableProperty]
    private string _rebuildButtonText = "全量重建索引";

    public ObservableCollection<string> IndexLogs { get; } = [];

    private bool _rebuildArmed;

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        if (IsIndexBusy) return;
        IsIndexBusy = true;
        IndexNote = "读取索引状态…";
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_index_status", null, timeoutSeconds: 60);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je)
            {
                IndexNote = $"读取失败：{resp.Error ?? "无数据"}";
                return;
            }
            var s = IndexStatusParser.Parse(je);
            ChunkCountText = s.Chunks > 0 ? s.Chunks.ToString() : "—";
            SessionCountText = s.Sessions > 0 ? s.Sessions.ToString() : "—";
            MessageCountText = s.TotalMsgs > 0 ? s.TotalMsgs.ToString() : "—";
            DimText = s.Dim > 0 ? s.Dim.ToString() : "—";
            BuiltAtText = s.BuiltAt ?? s.LastUpdated ?? "—";
            IndexNote = s.Error is not null
                ? $"索引不可用：{s.Error}"
                : s.Loaded
                    ? "本地索引已加载，可直接检索。"
                    : "索引未加载（点下面的「全量重建索引」）。";
        }
        catch (Exception ex)
        {
            IndexNote = $"读取异常：{ex.Message}";
        }
        finally
        {
            IsIndexBusy = false;
        }
    }

    /// <summary>增量补齐：只处理新消息（对话里"翻最新的聊天"查得到就靠它）。</summary>
    [RelayCommand]
    private async Task IncrementalIndexAsync()
    {
        if (IsIndexBusy) return;
        IsIndexBusy = true;
        ActionStatus = "增量补齐中（Bridge 阻塞式，完成后一次性显示日志）…";
        IndexLogs.Clear();
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_incremental_index",
                new Dictionary<string, object?> { ["hard_cap"] = 200, ["embed_batch"] = 32, ["sample_limit"] = 2000 },
                timeoutSeconds: 600);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je)
            {
                ActionStatus = $"增量补齐失败：{resp.Error ?? "无数据"}";
                return;
            }
            foreach (var l in ReadLogs(je)) IndexLogs.Add(l);
            var ok = je.TryGetProperty("ok", out var o) && o.GetBoolean();
            if (!ok)
            {
                ActionStatus = $"增量补齐失败：{GetStr(je, "error")}";
                return;
            }
            var added = GetInt(je, "added_chunks");
            var msgs = GetInt(je, "new_messages");
            ActionStatus = added == 0
                ? "增量补齐完成：没有新消息（幂等）。"
                : $"增量补齐完成：新增 {added} 个记忆片段 / {msgs} 条消息。";
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ActionStatus = $"增量补齐异常：{ex.Message}";
        }
        finally
        {
            IsIndexBusy = false;
        }
    }

    /// <summary>全量重建（要连点两次确认；重建期间 Bridge 忙，别误触）。</summary>
    [RelayCommand]
    private async Task RebuildIndexAsync()
    {
        if (IsIndexBusy) return;
        if (!_rebuildArmed)
        {
            _rebuildArmed = true;
            RebuildButtonText = "再点一次确认全量重建";
            ActionStatus = "全量重建会重扫全部聊天记录（约数分钟），确认后再点一次。";
            return;
        }
        _rebuildArmed = false;
        RebuildButtonText = "全量重建索引";
        IsIndexBusy = true;
        ActionStatus = "全量重建中（Bridge 阻塞式，完成后一次性显示日志）…";
        IndexLogs.Clear();
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_build_index",
                new Dictionary<string, object?> { ["scope"] = "all", ["recent_days"] = 0 },
                timeoutSeconds: 900);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je)
            {
                ActionStatus = $"全量重建失败：{resp.Error ?? "无数据"}";
                return;
            }
            foreach (var l in ReadLogs(je)) IndexLogs.Add(l);
            var ok = je.TryGetProperty("ok", out var o) && o.GetBoolean();
            ActionStatus = ok
                ? $"全量重建完成：{GetInt(je, "chunks")} 个片段 / {GetInt(je, "sessions")} 个会话 / {GetNum(je, "elapsed_sec")}s"
                : $"全量重建失败：{GetStr(je, "error")}";
            if (ok) await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ActionStatus = $"全量重建异常：{ex.Message}";
        }
        finally
        {
            IsIndexBusy = false;
        }
    }

    // ---- 检索演示（知识库"能查"的直接证据） ----

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private bool _isSearching;

    /// <summary>检索过程说明（"召回 5 条相关记忆 · 向量+全文混合"），空则不显示。</summary>
    [ObservableProperty]
    private string _searchNote = "";

    [ObservableProperty]
    private string _searchHint = "输入一句话，看 Agent 的知识库里能召回什么（例：实习经历 / 秋招 / 房租）。";

    public ObservableCollection<MemoryCardItem> RecallItems { get; } = [];

    [RelayCommand]
    private async Task SearchAsync()
    {
        var q = Query.Trim();
        if (q.Length == 0 || IsSearching) return;
        if (_services.HybridRetriever is null)
        {
            SearchHint = "检索器未就绪（Bridge 可能未启动）。";
            return;
        }

        IsSearching = true;
        RecallItems.Clear();
        SearchHint = "";
        SearchNote = $"知识库检索：「{q}」…";
        try
        {
            var hits = await _services.HybridRetriever.SearchAsync(q, TopK);
            var rank = 1;
            foreach (var h in hits)
            {
                RecallItems.Add(new MemoryCardItem(
                    rank++,
                    string.IsNullOrWhiteSpace(h.SessionName) ? h.SessionId : h.SessionName,
                    string.IsNullOrWhiteSpace(h.Date) ? "未知日期" : h.Date,
                    Preview(h.Text),
                    h.Score > 0 ? $"相关度 {h.Score:0.00}" : "",
                    SourceText(h.Source),
                    h.MsgCount > 0 ? $"{h.MsgCount} 条消息" : ""));
            }
            SearchNote = hits.Count == 0
                ? $"知识库检索：「{q}」→ 没有召回任何记忆（换一个词试试）。"
                : $"知识库检索：「{q}」→ 召回 {hits.Count} 条相关记忆（向量 + 全文混合检索）。";
        }
        catch (Exception ex)
        {
            SearchNote = $"检索失败：{ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>片段可能很长，卡片里只放开头一段（完整内容在对话的回答证据里能看到）。</summary>
    private static string Preview(string text)
    {
        var t = (text ?? "").Replace("\r", "").Replace('\n', ' ').Trim();
        return t.Length <= 160 ? t : t[..160] + "…";
    }

    private static string SourceText(string source) => source switch
    {
        "vector" => "向量检索",
        "fts" => "关键词检索",
        "hybrid" => "混合检索",
        _ => source,
    };

    private static IEnumerable<string> ReadLogs(System.Text.Json.JsonElement je)
    {
        if (je.TryGetProperty("progress_logs", out var logs) && logs.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var l in logs.EnumerateArray())
                if (l.ValueKind == System.Text.Json.JsonValueKind.String)
                    yield return l.GetString() ?? "";
        }
    }

    private static string GetStr(System.Text.Json.JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int GetInt(System.Text.Json.JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : 0;

    private static double GetNum(System.Text.Json.JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : 0;
}
