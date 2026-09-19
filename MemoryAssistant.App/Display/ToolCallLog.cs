using System.Collections.ObjectModel;

namespace MemoryAssistant.App.Display;

/// <summary>一条工具调用记录（展示用）。</summary>
public sealed record ToolCallRecord(
    string Name,
    string Args,
    string Result,
    bool Success,
    double ElapsedMs,
    DateTimeOffset At)
{
    /// <summary>调用时刻（HH:mm:ss）。</summary>
    public string TimeText => At.ToString("HH:mm:ss");

    /// <summary>状态（配 ToolStateToBrushConverter 上色）。</summary>
    public string State => Success ? "ok" : "failed";

    public string StateGlyph => Success ? "✓" : "✕";

    public string ElapsedText => ElapsedMs <= 0 ? "" : $"{ElapsedMs:0}ms";

    /// <summary>一行标题："search_messages(keyword=秋招)"。</summary>
    public string Headline => Args.Length == 0 ? Name : $"{Name}({Args})";
}

/// <summary>
/// 工具调用台账（展示用）：把对话过程中实际发生的每次工具调用留一条记录，
/// 供「工具」页展示"最近调用记录"——让老师看到工具不只是清单上的名字，而是真被调过。
///
/// 纯进程内内存态、有上限、不落盘、不参与 Agent 决策，也不改任何后端行为。
/// </summary>
public sealed class ToolCallLog
{
    private const int MaxRecords = 60;

    /// <summary>最近调用（新的在前）。</summary>
    public ObservableCollection<ToolCallRecord> Recent { get; } = [];

    /// <summary>累计调用次数（跨对话保留，展示"这个 Agent 干活了多久"）。</summary>
    public int TotalCount { get; private set; }

    public void Record(string name, string args, string result, bool success, double elapsedMs = 0)
    {
        TotalCount++;
        Recent.Insert(0, new ToolCallRecord(
            name ?? "",
            args ?? "",
            result ?? "",
            success,
            elapsedMs,
            DateTimeOffset.Now));
        while (Recent.Count > MaxRecords) Recent.RemoveAt(Recent.Count - 1);
    }
}
