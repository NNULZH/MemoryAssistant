namespace MemoryAssistant.Core.Agent.Runtime;

/// <summary>
/// 任务临时工作区（plan2 §3.3）：记录目标 / 已知事实 / 缺口 / 下一步。
/// 与最终回答严格分离，不向 UI 直接暴露模型私有思维链。
/// </summary>
public sealed class Scratchpad
{
    public string Goal { get; set; } = "";
    public List<string> Known { get; } = [];
    public List<string> Missing { get; } = [];
    public List<string> NextActions { get; } = [];

    private readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);

    public void AddKnown(string fact) => Known.Add(fact);
    public void AddMissing(string gap) => Missing.Add(gap);
    public void SetNext(params string[] actions)
    {
        NextActions.Clear();
        NextActions.AddRange(actions);
    }

    public void SetNote(string key, string value) => _notes[key] = value;
    public bool TryGetNote(string key, out string value) => _notes.TryGetValue(key, out value!);

    /// <summary>紧凑的文本摘要（供 Trace / 日志展示，非最终回答）。</summary>
    public string ToSummary()
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(Goal)) sb.AppendLine($"goal: {Goal}");
        if (Known.Count > 0) sb.AppendLine($"known: {string.Join("; ", Known)}");
        if (Missing.Count > 0) sb.AppendLine($"missing: {string.Join("; ", Missing)}");
        if (NextActions.Count > 0) sb.AppendLine($"next: {string.Join("; ", NextActions)}");
        return sb.ToString().Trim();
    }
}
