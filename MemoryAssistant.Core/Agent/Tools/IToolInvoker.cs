namespace MemoryAssistant.Core.Agent.Tools;

/// <summary>外部工具的调用方式声明（当前支持 HTTP）。</summary>
public sealed record RemoteToolEndpoint
{
    /// <summary>GET | POST（GET 时把参数按 {名字} 填进 URL，POST 时作为 JSON body）。</summary>
    public string Method { get; init; } = "GET";

    /// <summary>URL 模板，形如 https://host/path?city={city}</summary>
    public string Url { get; init; } = "";
}

/// <summary>
/// 外部工具的实际执行器。Core 只依赖这个抽象（便于单测与替换），
/// HttpClient 等具体实现放在 Infrastructure。
/// </summary>
public interface IToolInvoker
{
    Task<ToolCallResult> InvokeAsync(
        RemoteToolEndpoint endpoint,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken ct);
}
