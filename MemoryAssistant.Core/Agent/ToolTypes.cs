using System.Text.Json.Nodes;

namespace MemoryAssistant.Core.Agent;

/// <summary>参数 JSON 类型（OpenAPI/function calling）。</summary>
public static class ToolParamType
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Boolean = "boolean";
    public const string Number = "number";

    public static readonly string[] Allowed = [String, Integer, Boolean, Number];
}

/// <summary>
/// 一个工具参数的声明式描述（plan2 §6.1：name/type/description/required/enum/range）。
/// ToolRegistry 据此生成真实 JSON Schema，ToolProvider 据此做默认值填充与参数校验。
/// </summary>
public sealed record ToolParameterSpec
{
    public string Name { get; init; } = "";
    /// <summary>string | integer | boolean | number。</summary>
    public string Type { get; init; } = ToolParamType.String;
    public string? Description { get; init; }
    public bool Required { get; init; }
    /// <summary>可选默认值（string/int/bool）。</summary>
    public object? Default { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    /// <summary>可选枚举白名单。</summary>
    public IReadOnlyList<string>? Enum { get; init; }
}

/// <summary>工具分类（plan2 §6.2）；2.0 默认只开放只读工具（Action 预留）。</summary>
public static class ToolCategory
{
    public const string Memory = "Memory";
    public const string Search = "Search";
    public const string Analysis = "Analysis";
    public const string Context = "Context";
    public const string System = "System";
    public const string Action = "Action"; // 预留：写/行动类，2.0 不开放

    public static readonly string[] Allowed = [Memory, Search, Analysis, Context, System, Action];
}

/// <summary>一个工具的运行时定义：Schema 元数据 + 执行函数。</summary>
public sealed record ToolDefinition
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = ToolCategory.Memory;
    /// <summary>参数声明（真实 schema 的来源）。</summary>
    public IReadOnlyList<ToolParameterSpec> Parameters { get; init; } = [];
    /// <summary>只读标记：true=安全只读，Action 类必须显式置 false。</summary>
    public bool ReadOnly { get; init; } = true;
    public Func<string, CancellationToken, Task<ToolCallResult>> ExecuteAsync { get; init; } = null!;
}

/// <summary>
/// 工具的参数 JSON Schema（OpenAI function calling 格式）。
/// 业务层用 JsonObject 构建，Core 只负责透传。
/// </summary>
public sealed record ToolSchema
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>JSON Schema 对象（parameters）。</summary>
    public JsonObject? Parameters { get; init; }
}

/// <summary>一次工具调用请求（由模型发出）。</summary>
public sealed record ToolCallRequest
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    /// <summary>参数 JSON 字符串（反序列化交给具体工具）。</summary>
    public string ArgumentsJson { get; init; } = "{}";
}

/// <summary>工具调用的执行结果。</summary>
public sealed record ToolCallResult
{
    public string ToolCallId { get; init; } = "";
    public bool Success { get; init; }
    public string Output { get; init; } = "";
    public string? Error { get; init; }
    public double ElapsedMs { get; init; }
    public bool IsOmitted { get; init; }
}
