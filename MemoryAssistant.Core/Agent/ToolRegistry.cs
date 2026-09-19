using System.Text.Json.Nodes;

namespace MemoryAssistant.Core.Agent;

/// <summary>
/// 工具注册表。业务层注册"所见即所得"的工具（含真实参数声明），
/// AgentLoop 通过它获取 schema 与执行函数。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.Ordinal);

    public void Register(ToolDefinition tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name)) throw new ArgumentException("Tool name required");
        if (!ToolCategory.Allowed.Contains(tool.Category, StringComparer.Ordinal))
            throw new ArgumentException($"非法工具分类: {tool.Category}");
        if (tool.Category == ToolCategory.Action && tool.ReadOnly)
            throw new ArgumentException($"Action 类工具 {tool.Name} 必须显式 ReadOnly=false");
        foreach (var p in tool.Parameters)
        {
            if (!ToolParamType.Allowed.Contains(p.Type, StringComparer.Ordinal))
                throw new ArgumentException($"非法参数类型 {p.Type}（tool={tool.Name} param={p.Name}）");
        }
        _tools[tool.Name] = tool;
    }

    public bool TryGet(string name, out ToolDefinition tool) => _tools.TryGetValue(name, out tool!);

    /// <summary>注销一个工具（V3.5 热注册用：外部清单被移除/改写时要能撤下旧工具）。</summary>
    public bool Unregister(string name) => _tools.Remove(name);

    public IReadOnlyCollection<ToolDefinition> All => _tools.Values;

    /// <summary>
    /// 依据参数声明生成真实 JSON Schema（plan2 §6.1）：
    /// { type:object, properties:{ name: {type,description,enum?,minimum?,maximum?} }, required:[...] }。
    /// 修复旧实现空 properties 的技术债：模型现在能看到每个工具的真实参数协议。
    /// </summary>
    public IReadOnlyList<ToolSchema> BuildSchemas()
    {
        return _tools.Values.Select(t => new ToolSchema
        {
            Name = t.Name,
            Description = $"[{t.Category}] {t.Description}",
            Parameters = BuildParameters(t),
        }).ToList();
    }

    /// <summary>整数范围内存整型，避免 JSON 出现 1.0 形式的数字（OpenAI/DeepSeek 都接受，但更干净）。</summary>
    private static JsonValue NumberValue(double value)
        => value % 1 == 0 ? JsonValue.Create((long)value) : JsonValue.Create(value);

    private static JsonObject BuildParameters(ToolDefinition tool)
    {
        var properties = new JsonObject();
        foreach (var p in tool.Parameters)
        {
            var node = new JsonObject { ["type"] = p.Type };
            if (!string.IsNullOrWhiteSpace(p.Description))
                node["description"] = p.Description;
            if (p.Enum is { Count: > 0 })
            {
                var arr = new JsonArray();
                foreach (var e in p.Enum) arr.Add(e);
                node["enum"] = arr;
            }
            if (p.Minimum is not null) node["minimum"] = NumberValue(p.Minimum.Value);
            if (p.Maximum is not null) node["maximum"] = NumberValue(p.Maximum.Value);
            properties[p.Name] = node;
        }

        var required = new JsonArray();
        foreach (var p in tool.Parameters.Where(p => p.Required))
            required.Add(p.Name);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }
}
