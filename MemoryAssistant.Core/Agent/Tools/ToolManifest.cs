using System.Text.Json;
using System.Text.Json.Nodes;

namespace MemoryAssistant.Core.Agent.Tools;

/// <summary>清单里的一个外部工具声明。</summary>
public sealed record ManifestToolDefinition
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = ToolCategory.Context;
    public bool ReadOnly { get; init; } = true;
    public IReadOnlyList<ToolParameterSpec> Parameters { get; init; } = [];
    public RemoteToolEndpoint Endpoint { get; init; } = new();
}

/// <summary>
/// 工具清单（V3.5）：用一份 JSON 声明"这台机器上还有哪些工具可由智能体调用"。
/// 目的是让"新增能力"不必改代码——把清单文件丢进 tools 目录，智能体下一轮就能看到并使用。
/// 解析全程容错：单条声明不合法只跳过该条（并给出原因），不影响其它工具上架。
/// </summary>
public static class ToolManifest
{
    public const string FileSearchPattern = "*.tool.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析清单，返回 (可用工具, 被跳过的原因)。</summary>
    public static (IReadOnlyList<ManifestToolDefinition> Tools, IReadOnlyList<string> Rejected) Parse(string json)
    {
        var tools = new List<ManifestToolDefinition>();
        var rejected = new List<string>();

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return (tools, [$"清单不是合法 JSON：{ex.Message}"]);
        }

        var array = root is JsonObject obj && obj["tools"] is JsonArray arr ? arr : null;
        if (array is null) return (tools, ["清单缺少 tools 数组"]);

        foreach (var node in array)
        {
            if (node is not JsonObject item)
            {
                rejected.Add("存在非对象条目");
                continue;
            }

            var dto = item.Deserialize<ManifestToolDto>(JsonOpts) ?? new ManifestToolDto();
            var reason = Validate(dto);
            if (reason is not null)
            {
                rejected.Add($"{dto.Name ?? "(未命名)"}：{reason}");
                continue;
            }

            tools.Add(new ManifestToolDefinition
            {
                Name = dto.Name!.Trim(),
                Description = dto.Description ?? "",
                Category = string.IsNullOrWhiteSpace(dto.Category) ? ToolCategory.Context : dto.Category!,
                ReadOnly = dto.ReadOnly,
                Parameters = (dto.Parameters ?? []).Select(p => new ToolParameterSpec
                {
                    Name = p.Name ?? "",
                    Type = string.IsNullOrWhiteSpace(p.Type) ? ToolParamType.String : p.Type!,
                    Description = p.Description,
                    Required = p.Required,
                    Default = p.Default is { } dv ? FromJsonElement(dv) : null,
                    Minimum = p.Minimum,
                    Maximum = p.Maximum,
                    Enum = p.Enum,
                }).ToList(),
                Endpoint = new RemoteToolEndpoint
                {
                    Method = string.IsNullOrWhiteSpace(dto.Method) ? "GET" : dto.Method!.ToUpperInvariant(),
                    Url = dto.Url ?? "",
                },
            });
        }

        return (tools, rejected);
    }

    /// <summary>把清单声明包装成可注册的 ToolDefinition（执行时交给 invoker）。</summary>
    public static IReadOnlyList<ToolDefinition> ToToolDefinitions(
        IEnumerable<ManifestToolDefinition> definitions, IToolInvoker invoker)
    {
        var list = new List<ToolDefinition>();
        foreach (var d in definitions)
        {
            var endpoint = d.Endpoint;
            var parameters = d.Parameters;
            list.Add(new ToolDefinition
            {
                Name = d.Name,
                Description = d.Description,
                Category = d.Category,
                ReadOnly = d.ReadOnly,
                Parameters = parameters,
                ExecuteAsync = (argsJson, ct) =>
                    invoker.InvokeAsync(endpoint, ParseArguments(argsJson, parameters), ct),
            });
        }
        return list;
    }

    /// <summary>解析调用参数，并按声明补默认值（模型可能不传可选参数）。</summary>
    private static Dictionary<string, object?> ParseArguments(
        string argsJson, IReadOnlyList<ToolParameterSpec> specs)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        args[p.Name] = p.Value.ValueKind switch
                        {
                            JsonValueKind.String => p.Value.GetString(),
                            JsonValueKind.Number => p.Value.TryGetInt64(out var l) ? l : p.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => null,
                            _ => p.Value.GetRawText(),
                        };
                    }
                }
            }
            catch (JsonException)
            {
                // 参数不合法时按"全默认"处理，由工具自身报错；不在这里抛
            }
        }

        foreach (var spec in specs)
        {
            if (spec.Default is not null && !args.ContainsKey(spec.Name))
                args[spec.Name] = spec.Default;
        }
        return args;
    }

    /// <summary>把 JSON 默认值转成好用的 CLR 值（整数归一为 long，避免出现 1.0 形式）。</summary>
    private static object? FromJsonElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static string? Validate(ManifestToolDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return "缺少 name";
        if (!ToolCategory.Allowed.Contains(dto.Category ?? ToolCategory.Context, StringComparer.Ordinal))
            return $"非法分类 {dto.Category}";
        if (dto.Category == ToolCategory.Action && dto.ReadOnly)
            return "Action 类工具必须显式 readOnly=false";
        if (string.IsNullOrWhiteSpace(dto.Url)) return "缺少 url";
        var method = (dto.Method ?? "GET").ToUpperInvariant();
        if (method is not ("GET" or "POST")) return $"暂不支持的方法 {method}";
        foreach (var p in dto.Parameters ?? [])
        {
            if (string.IsNullOrWhiteSpace(p.Name)) return "存在无名参数";
            if (!ToolParamType.Allowed.Contains(p.Type ?? ToolParamType.String, StringComparer.Ordinal))
                return $"参数 {p.Name} 类型非法 {p.Type}";
        }
        return null;
    }

    private sealed class ManifestToolDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public bool ReadOnly { get; set; } = true;
        public string? Method { get; set; }
        public string? Url { get; set; }
        public List<ParamDto>? Parameters { get; set; }
    }

    private sealed class ParamDto
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? Description { get; set; }
        public bool Required { get; set; }
        public JsonElement? Default { get; set; }
        public double? Minimum { get; set; }
        public double? Maximum { get; set; }
        public List<string>? Enum { get; set; }
    }
}
