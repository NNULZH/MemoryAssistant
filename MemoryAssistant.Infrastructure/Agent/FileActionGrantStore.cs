using System.Text.Json;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Infrastructure.Agent;

/// <summary>
/// 写操作授权的落盘实现（第三阶段补充：授权机制）——data/action-grants.json。
///
/// 为什么落盘：授权是"以后不用再问"的承诺，进程一重启就失效等于每次都要重新点一遍，
/// 用户会觉得这个机制没用。落盘位置与 missions.json 同一处（exe 同目录的 data/），
/// 用户想撤销就直接删掉对应那条。
///
/// 坏文件/读不到一律当作**没有任何授权**：宁多问一次，也不放行没被授权过的操作。
/// </summary>
public sealed class FileActionGrantStore : IActionGrantStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly IAppLogger? _logger;
    private readonly object _gate = new();
    private List<ActionGrant>? _cache;

    public FileActionGrantStore(string? path = null, IAppLogger? logger = null)
    {
        _path = path ?? Path.Combine(AppContext.BaseDirectory, "data", "action-grants.json");
        _logger = logger;
    }

    /// <summary>授权文件路径（便于日志核对 / 告知用户去哪儿撤销）。</summary>
    public string FilePath => _path;

    public bool IsGranted(string action, string target)
    {
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(target)) return false;
        lock (_gate)
            return Load().Any(g => Same(g.Action, action) && Same(g.Target, target));
    }

    public void Grant(string action, string target)
    {
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(target)) return;
        lock (_gate)
        {
            var items = Load();
            if (items.Any(g => Same(g.Action, action) && Same(g.Target, target))) return;
            items.Add(new ActionGrant
            {
                Action = action.Trim(),
                Target = target.Trim(),
                GrantedAt = DateTimeOffset.Now,
            });
            Save(items);
            _logger?.Info($"[Action] 已写入授权：{action} → 「{target.Trim()}」（{_path}）");
        }
    }

    public void Revoke(string action, string target)
    {
        lock (_gate)
        {
            var items = Load();
            if (items.RemoveAll(g => Same(g.Action, action) && Same(g.Target, target)) > 0) Save(items);
        }
    }

    public IReadOnlyList<ActionGrant> List()
    {
        lock (_gate) return Load().ToList();
    }

    private List<ActionGrant> Load()
    {
        if (_cache is not null) return _cache;
        if (!File.Exists(_path)) return _cache = [];
        try
        {
            return _cache = JsonSerializer.Deserialize<List<ActionGrant>>(File.ReadAllText(_path), JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            // 文件损坏不放行任何授权（宁可多问一次），但要留下痕迹，别让它悄悄变成"授权都没了"
            _logger?.Error($"[Action] 授权文件读取失败，按无授权处理：{ex.Message}（{_path}）");
            return _cache = [];
        }
    }

    private void Save(List<ActionGrant> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 先写临时文件再替换：避免写到一半断电留下半截 JSON（下轮就读不出来了）
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[Action] 授权写入失败（本次授权只在内存里生效）：{ex.Message}");
        }
    }

    private static bool Same(string a, string b)
        => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
