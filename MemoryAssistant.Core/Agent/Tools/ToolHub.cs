namespace MemoryAssistant.Core.Agent.Tools;

/// <summary>一次工具清单变化（供 UI/日志"智能体实时掌握新 tool"的可见性）。</summary>
public sealed record ToolHubChange(string Scope, IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Added.Count > 0) parts.Add($"+{Added.Count}：{string.Join("/", Added)}");
        if (Removed.Count > 0) parts.Add($"-{Removed.Count}：{string.Join("/", Removed)}");
        return parts.Count > 0 ? $"{Scope} {string.Join("，", parts)}" : $"{Scope} 无变化";
    }
}

/// <summary>
/// 工具热注册中心（V3.5）：按"来源（scope）"整体替换一组工具，并在变化时发通知。
/// 这样外部清单文件被新增/改写/删除时，注册表能同步上下架，而不是只能启动时注册一次。
/// 同一 scope 再次 Replace 会先撤下它上次登记的工具，避免残留失效工具。
/// </summary>
public sealed class ToolHub(ToolRegistry registry)
{
    private readonly ToolRegistry _registry = registry;
    private readonly Dictionary<string, List<string>> _scopes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>清单变化时触发（参数是本次变化摘要）。</summary>
    public event Action<ToolHubChange>? Changed;

    /// <summary>已登记的来源数。</summary>
    public int ScopeCount => _scopes.Count;

    /// <summary>已登记的来源名（清单重载时用来发现"文件已删除"的来源）。</summary>
    public IReadOnlyList<string> ScopeNames => _scopes.Keys.ToList();

    /// <summary>某来源当前登记的工具名。</summary>
    public IReadOnlyList<string> ToolsOf(string scope)
        => _scopes.TryGetValue(scope, out var names) ? names : [];

    /// <summary>用给定工具整体替换某来源（先撤旧、再挂新），返回本次变化。</summary>
    public ToolHubChange ReplaceScope(string scope, IEnumerable<ToolDefinition> tools)
    {
        var added = new List<string>();
        var removed = new List<string>();

        if (_scopes.TryGetValue(scope, out var old))
        {
            foreach (var name in old)
            {
                _registry.Unregister(name);
                removed.Add(name);
            }
            _scopes.Remove(scope);
        }

        var names = new List<string>();
        foreach (var tool in tools)
        {
            _registry.Register(tool);
            names.Add(tool.Name);
            added.Add(tool.Name);
        }
        if (names.Count > 0) _scopes[scope] = names;

        var change = new ToolHubChange(scope, added, removed);
        if (added.Count > 0 || removed.Count > 0) Changed?.Invoke(change);
        return change;
    }

    /// <summary>撤下某来源的全部工具（清单文件被删除时用）。</summary>
    public ToolHubChange RemoveScope(string scope)
    {
        var removed = new List<string>();
        if (_scopes.TryGetValue(scope, out var old))
        {
            foreach (var name in old)
            {
                _registry.Unregister(name);
                removed.Add(name);
            }
            _scopes.Remove(scope);
        }

        var change = new ToolHubChange(scope, [], removed);
        if (removed.Count > 0) Changed?.Invoke(change);
        return change;
    }
}
