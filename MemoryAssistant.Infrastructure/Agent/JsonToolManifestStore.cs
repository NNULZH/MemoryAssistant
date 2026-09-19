using MemoryAssistant.Core.Agent.Tools;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Infrastructure.Agent;

/// <summary>
/// 工具清单仓库（V3.5）：扫一个目录下的 *.tool.json，逐个作为 scope 交给 ToolHub 热注册；
/// 并用 FileSystemWatcher 监听增删改，改动后（去抖）整体重载 —— 于是"往目录里丢一个文件"
/// 就等于"给智能体加了一个新工具"，无需重启。
/// </summary>
public sealed class JsonToolManifestStore : IDisposable
{
    private readonly string _dir;
    private readonly ToolHub _hub;
    private readonly IToolInvoker _invoker;
    private readonly IAppLogger? _logger;
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;

    public JsonToolManifestStore(string directory, ToolHub hub, IToolInvoker invoker, IAppLogger? logger = null)
    {
        _dir = directory;
        _hub = hub;
        _invoker = invoker;
        _logger = logger;
    }

    public string Directory => _dir;

    /// <summary>重载全部清单：每个文件一个 scope；已消失的文件对应的 scope 会被撤下。</summary>
    public IReadOnlyList<ToolHubChange> LoadAll()
    {
        var changes = new List<ToolHubChange>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            System.IO.Directory.CreateDirectory(_dir);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ToolHub] 无法创建工具清单目录 {_dir}：{ex.Message}");
            return changes;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, ToolManifest.FileSearchPattern))
        {
            seen.Add(file);
            try
            {
                var (defs, rejected) = ToolManifest.Parse(File.ReadAllText(file));
                foreach (var reason in rejected)
                    _logger?.Warn($"[ToolHub] 跳过 {System.IO.Path.GetFileName(file)} 的一条声明：{reason}");
                changes.Add(_hub.ReplaceScope(file, ToolManifest.ToToolDefinitions(defs, _invoker)));
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[ToolHub] 读取清单 {file} 失败：{ex.Message}");
            }
        }

        foreach (var scope in _hub.ScopeNames.Where(s => !seen.Contains(s)).ToList())
            changes.Add(_hub.RemoveScope(scope));

        return changes;
    }

    /// <summary>开始监听目录变化（去抖 500ms 后整体重载）。</summary>
    public void StartWatching()
    {
        if (_watcher is not null) return;
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            _watcher = new FileSystemWatcher(_dir, ToolManifest.FileSearchPattern)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, _) => ScheduleReload();
            _watcher.Changed += (_, _) => ScheduleReload();
            _watcher.Deleted += (_, _) => ScheduleReload();
            _watcher.Renamed += (_, _) => ScheduleReload();
            _debounce = new System.Threading.Timer(_ => ReloadSafely(), null,
                Timeout.Infinite, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ToolHub] 无法监听工具清单目录：{ex.Message}");
        }
    }

    private void ScheduleReload()
    {
        lock (_gate) _debounce?.Change(500, Timeout.Infinite);
    }

    private void ReloadSafely()
    {
        try
        {
            foreach (var change in LoadAll())
                _logger?.Info($"[ToolHub] 清单已更新：{change}");
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ToolHub] 重载清单失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
    }
}
