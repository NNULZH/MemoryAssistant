namespace MemoryAssistant.Core.PythonBridge;

/// <summary>Bridge 请求的统一响应。</summary>
public sealed record BridgeResponse(string Id, bool Success, object? Data, string? Error);

/// <summary>
/// Python Bridge 抽象。只暴露方法调用，不暴露进程细节，
/// 从而把 wxchat SDK 从 C# 业务层中隔离。
/// </summary>
public interface IPythonBridge : IDisposable
{
    bool IsRunning { get; }
    Task<bool> StartAsync(CancellationToken ct = default);
    Task StopAsync();
    Task<BridgeResponse> RequestAsync(
        string method,
        IReadOnlyDictionary<string, object?>? args = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default);
}