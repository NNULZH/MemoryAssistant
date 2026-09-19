using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryAssistant.Core.Configuration;

namespace MemoryAssistant.Infrastructure.Configuration;

/// <summary>
/// 从 appsettings.json（可选的 jsonPath）加载配置，再以字符串环境变量覆盖。
/// 有值则覆盖，空值不覆盖，便于在受限环境里保持默认。
///
/// 加载顺序（后者覆盖前者）：
///   appsettings.json（仓库内，不含机密）
///   appsettings.local.json（同目录，含本地机密，已 gitignore）
///   环境变量（LLM_API_KEY 等）
/// </summary>
public static class ConfigurationLoader
{
    public static AppSettings Load(string? jsonPath = null)
    {
        var basePath = jsonPath ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var dir = Path.GetDirectoryName(basePath) ?? AppContext.BaseDirectory;

        // 以"默认实例 + 按 JSON 深合并"的方式加载：**只覆盖文件里真实出现过的键**。
        // 不能"反序列化成 AppSettings 再逐属性合并"——文件中缺失的字段会被反序列化成类默认值
        // （例如 Model 的默认 deepseek-chat），于是 appsettings.local.json 里只要有一个 llm 键，
        // 就会把 appsettings.json 里显式配置的 model 悄悄冲掉（实测踩到过）。
        var merged = JsonSerializer.SerializeToNode(new AppSettings(), JsonOpts) as JsonObject ?? [];
        DeepMerge(merged, ReadObject(basePath));
        DeepMerge(merged, ReadObject(Path.Combine(dir, "appsettings.local.json")));

        var settings = merged.Deserialize<AppSettings>(JsonOpts) ?? new AppSettings();
        Override(settings);
        return settings;
    }

    private static JsonObject? ReadObject(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            // 配置文件写坏时不让整个应用起不来：跳过该文件，用已有值继续。
            return null;
        }
    }

    /// <summary>把 src 真实存在的键递归合并进 dst（对象递归合并，其余整体替换）。</summary>
    private static void DeepMerge(JsonObject dst, JsonObject? src)
    {
        if (src is null) return;
        foreach (var (key, value) in src)
        {
            if (value is JsonObject srcObj && dst[key] is JsonObject dstObj)
                DeepMerge(dstObj, srcObj);
            else
                dst[key] = value?.DeepClone();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static void Override(AppSettings s)
    {
        OverrideScalar(s.Llm.BaseUrl, v => s.Llm.BaseUrl = v, "LLM_BASE_URL");
        OverrideScalar(s.Llm.ApiKey, v => s.Llm.ApiKey = v, "LLM_API_KEY");
        OverrideScalar(s.Llm.Model, v => s.Llm.Model = v, "LLM_MODEL");
        OverrideDouble(s.Llm.Temperature, v => s.Llm.Temperature = v, "LLM_TEMPERATURE");

        OverrideScalar(s.Embedding.Backend, v => s.Embedding.Backend = v, "EMBED_BACKEND");
        OverrideScalar(s.Embedding.Model, v => s.Embedding.Model = v, "EMBED_MODEL");
        OverrideScalar(s.Embedding.ApiKey, v => s.Embedding.ApiKey = v, "EMBED_API_KEY");

        OverrideInt(s.Rag.TopK, v => s.Rag.TopK = v, "RAG_TOP_K");
        OverrideInt(s.Rag.ChunkSize, v => s.Rag.ChunkSize = v, "RAG_CHUNK_SIZE");

        OverrideInt(s.Agent.MaxRounds, v => s.Agent.MaxRounds = v, "AGENT_MAX_ROUNDS");
        OverrideInt(s.Agent.MaxToolResultChars, v => s.Agent.MaxToolResultChars = v, "AGENT_MAX_TOOL_RESULT_CHARS");
        OverrideInt(s.Agent.MaxPlanSteps, v => s.Agent.MaxPlanSteps = v, "AGENT_MAX_PLAN_STEPS");
        OverrideInt(s.Agent.MaxToolCalls, v => s.Agent.MaxToolCalls = v, "AGENT_MAX_TOOL_CALLS");
        OverrideInt(s.Agent.MaxFailures, v => s.Agent.MaxFailures = v, "AGENT_MAX_FAILURES");
        OverrideInt(s.Agent.MaxTaskSeconds, v => s.Agent.MaxTaskSeconds = v, "AGENT_MAX_TASK_SECONDS");
        OverrideBool(s.Agent.EcoMode, v => s.Agent.EcoMode = v, "AGENT_ECO_MODE");
        OverrideBool(s.Agent.SuppressAutoReply, v => s.Agent.SuppressAutoReply = v, "AGENT_SUPPRESS_AUTO_REPLY");
        OverrideBool(s.Agent.EnableReplanning, v => s.Agent.EnableReplanning = v, "AGENT_ENABLE_REPLANNING");
        OverrideInt(s.Agent.MaxReplanRounds, v => s.Agent.MaxReplanRounds = v, "AGENT_MAX_REPLAN_ROUNDS");

        OverrideScalar(s.WxChat.DataDir, v => s.WxChat.DataDir = v, "WXCHAT_DATA_DIR");
        OverrideScalar(s.WxChat.SnapshotDir, v => s.WxChat.SnapshotDir = v, "WXCHAT_SNAPSHOT_DIR");

        OverrideScalar(s.PythonBridge.PythonExePath, v => s.PythonBridge.PythonExePath = v, "PYTHON_EXE_PATH");
        OverrideScalar(s.PythonBridge.BridgeScriptPath, v => s.PythonBridge.BridgeScriptPath = v, "BRIDGE_SCRIPT_PATH");
        OverrideInt(s.PythonBridge.IdleTimeoutSeconds, v => s.PythonBridge.IdleTimeoutSeconds = v, "BRIDGE_IDLE_TIMEOUT_SECONDS");

        OverrideInt(s.Runtime.TimeoutSeconds, v => s.Runtime.TimeoutSeconds = v, "RUNTIME_TIMEOUT_SECONDS");
        OverrideInt(s.Runtime.RetryCount, v => s.Runtime.RetryCount = v, "RUNTIME_RETRY_COUNT");
    }

    private static void OverrideScalar(string current, Action<string> setter, string envName)
    {
        var v = System.Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(v)) setter(v);
    }

    private static void OverrideInt(int current, Action<int> setter, string envName)
    {
        if (int.TryParse(System.Environment.GetEnvironmentVariable(envName), out var v)) setter(v);
    }

    private static void OverrideDouble(double current, Action<double> setter, string envName)
    {
        if (double.TryParse(System.Environment.GetEnvironmentVariable(envName), out var v)) setter(v);
    }

    private static void OverrideBool(bool current, Action<bool> setter, string envName)
    {
        var raw = System.Environment.GetEnvironmentVariable(envName);
        if (raw is not null && (raw.Equals("1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)))
            setter(true);
        else if (raw is not null && (raw.Equals("0", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("false", StringComparison.OrdinalIgnoreCase)))
            setter(false);
    }
}