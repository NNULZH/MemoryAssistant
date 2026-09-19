using System.Text.Json;
using System.Text.Json.Serialization;
using MemoryAssistant.Core.Missions;

namespace MemoryAssistant.Infrastructure.Missions;

/// <summary>任务持久化（JSON 文件，plan V3.2）。默认 data/missions.json（相对当前工作目录）。</summary>
public sealed class JsonMissionRepository : IMissionRepository
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonMissionRepository(string? path = null)
    {
        _path = path ?? ResolveDefaultPath();
    }

    /// <summary>
    /// 默认落盘位置：exe 同目录的 data/missions.json（与 appsettings.json 的定位方式一致）。
    /// 这样"双击 exe / 从终端启动 / 从 IDE 启动"看到的是同一份任务，不会出现"任务去哪了"。
    /// 若旧版按"当前工作目录"存过任务且新位置还没文件，则继续用旧文件（迁移兼容）。
    /// </summary>
    private static string ResolveDefaultPath()
    {
        var current = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "missions.json");
        var legacy = System.IO.Path.Combine(System.Environment.CurrentDirectory, "data", "missions.json");
        if (!File.Exists(current) && File.Exists(legacy) &&
            !string.Equals(legacy, current, StringComparison.OrdinalIgnoreCase))
            return legacy;
        return current;
    }

    /// <summary>任务文件路径（便于日志/测试查看）。</summary>
    public string FilePath => _path;

    public IReadOnlyList<MissionDefinition> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var dto = JsonSerializer.Deserialize<MissionFile>(File.ReadAllText(_path), JsonOpts);
            if (dto?.Items is null) return [];
            return dto.Items.Select(d =>
            {
                var m = new MissionDefinition
                {
                    Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id,
                    Title = d.Title ?? "",
                    Goal = d.Goal ?? "",
                    Trigger = d.Trigger,
                    Action = d.Action,
                    IntervalMinutes = d.IntervalMinutes <= 0 ? 30 : d.IntervalMinutes,
                    IntervalSeconds = d.IntervalSeconds,
                    Target = d.Target ?? "",
                    RequiresApproval = d.RequiresApproval,
                };
                m.Restore(d.Status, d.LastRunAt, d.LastResult, d.WatchProfile, d.RunCount, d.Origin);
                return m;
            }).ToList();
        }
        catch
        {
            return []; // 文件损坏不阻塞启动
        }
    }

    public void Save(IEnumerable<MissionDefinition> missions)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var file = new MissionFile
        {
            Items = missions.Select(m => new MissionDto
            {
                Id = m.Id,
                Title = m.Title,
                Goal = m.Goal,
                Trigger = m.Trigger,
                Action = m.Action,
                IntervalMinutes = m.IntervalMinutes,
                IntervalSeconds = m.IntervalSeconds,
                Target = m.Target,
                RequiresApproval = m.RequiresApproval,
                Status = m.Status,
                LastRunAt = m.LastRunAt,
                LastResult = m.LastResult,
                WatchProfile = m.WatchProfile,
                RunCount = m.RunCount,
                Origin = m.Origin,
            }).ToList(),
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(file, JsonOpts));
    }

    private sealed class MissionFile
    {
        public List<MissionDto> Items { get; set; } = [];
    }

    private sealed class MissionDto
    {
        public Guid Id { get; set; }
        public string? Title { get; set; }
        public string? Goal { get; set; }
        public MissionTriggerKind Trigger { get; set; }
        /// <summary>跑完做什么（V3.7）。旧文件没有这个键 → 反序列化为 Summarize，行为不变。</summary>
        public MissionActionKind Action { get; set; }
        public int IntervalMinutes { get; set; }
        /// <summary>秒级间隔（V4.1，"每 30 秒"）。旧文件没有这个键 → 0，行为不变（走分钟）。</summary>
        public int IntervalSeconds { get; set; }
        public string? Target { get; set; }
        public bool RequiresApproval { get; set; }
        public MissionStatus Status { get; set; }
        public DateTimeOffset? LastRunAt { get; set; }
        public string? LastResult { get; set; }
        /// <summary>主题监听画像（V4.1，JSON 字符串）。旧文件没有这个键 → null，行为不变。</summary>
        public string? WatchProfile { get; set; }
        /// <summary>累计执行次数（课程展示）。旧文件没有这个键 → 0，行为不变。</summary>
        public int RunCount { get; set; }
        /// <summary>创建方式：agent / user（第三阶段 §23）。旧文件没有这个键 → null（按手工创建展示）。</summary>
        public string? Origin { get; set; }
    }
}
