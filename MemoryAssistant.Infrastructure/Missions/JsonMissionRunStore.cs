using System.Text.Json;
using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.Missions;

namespace MemoryAssistant.Infrastructure.Missions;

/// <summary>
/// 任务执行记录的落盘实现（第三阶段补充）：data/mission-runs.json。
///
/// 为什么必须落盘：这是"任务干过什么"的长期记忆——用户要能翻回上周三那次到底跑了什么，
/// 也应当能删掉某一条、清空全部。调度器里的 Log 是内存滚动日志（重启即空、结果截断），
/// 只能当实时滚动条看，不能当记忆。
///
/// 坏文件/读不到一律当作"没有记录"（宁少显示，也不要因为一个坏 JSON 让任务页打不开）。
/// </summary>
public sealed class JsonMissionRunStore : IMissionRunStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>最多保留多少条（超出丢最旧的）：账本是给人看的，不是无限日志。</summary>
    private const int MaxRecords = 500;

    private readonly string _path;
    private readonly IAppLogger? _logger;
    private readonly object _gate = new();
    private List<MissionRunRecord>? _cache;

    public JsonMissionRunStore(string? path = null, IAppLogger? logger = null)
    {
        _path = path ?? Path.Combine(AppContext.BaseDirectory, "data", "mission-runs.json");
        _logger = logger;
    }

    public string Location => _path;

    public void Append(MissionRunRecord record)
    {
        lock (_gate)
        {
            var items = Load();
            items.Add(record);
            if (items.Count > MaxRecords)
                items.RemoveRange(0, items.Count - MaxRecords);   // 保留最新
            Save(items);
        }
    }

    public IReadOnlyList<MissionRunRecord> List(Guid? missionId = null, int limit = 200)
    {
        lock (_gate)
            return Load()
                .Where(r => missionId is null || r.MissionId == missionId)
                .OrderByDescending(r => r.At)
                .Take(Math.Max(1, limit))
                .ToList();
    }

    public bool Delete(Guid recordId)
    {
        lock (_gate)
        {
            var items = Load();
            if (items.RemoveAll(r => r.Id == recordId) == 0) return false;
            Save(items);
            return true;
        }
    }

    public int Clear(Guid? missionId = null)
    {
        lock (_gate)
        {
            var items = Load();
            var removed = items.RemoveAll(r => missionId is null || r.MissionId == missionId);
            if (removed > 0) Save(items);
            return removed;
        }
    }

    private List<MissionRunRecord> Load()
    {
        if (_cache is not null) return _cache;
        if (!File.Exists(_path)) return _cache = [];
        try
        {
            return _cache = JsonSerializer.Deserialize<List<MissionRunRecord>>(File.ReadAllText(_path), Opts) ?? [];
        }
        catch (Exception ex)
        {
            _logger?.Error($"[Mission] 执行记录读取失败，按无记录处理：{ex.Message}（{_path}）");
            return _cache = [];
        }
    }

    private void Save(List<MissionRunRecord> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 先写临时文件再替换：避免写到一半断电留下半截 JSON（下轮就读不出来了）
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, Opts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[Mission] 执行记录写入失败（本次只在内存里生效）：{ex.Message}");
        }
    }
}
