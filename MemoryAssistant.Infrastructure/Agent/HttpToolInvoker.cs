using System.Net.Http;
using System.Text;
using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Tools;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Infrastructure.Agent;

/// <summary>
/// 通过 HTTP 调用外部工具（V3.5）：
/// - GET：把参数按 {名字} 填进 URL 模板（URL 编码）；
/// - POST：把参数字典序列化为 JSON 作为请求体；
/// 输出超过上限时截断（避免把超大响应灌进上下文）。异常一律转成失败结果，不抛出。
/// </summary>
public sealed class HttpToolInvoker(IAppLogger? logger = null) : IToolInvoker, IDisposable
{
    private const int MaxOutputChars = 4000;

    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly IAppLogger? _logger = logger;

    public async Task<ToolCallResult> InvokeAsync(
        RemoteToolEndpoint endpoint,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        double Elapsed() => (DateTime.UtcNow - started).TotalMilliseconds;

        try
        {
            using var resp = endpoint.Method == "POST"
                ? await _client.PostAsync(endpoint.Url,
                    new StringContent(JsonSerializer.Serialize(args), Encoding.UTF8, "application/json"), ct)
                : await _client.GetAsync(BuildUrl(endpoint.Url, args), ct);

            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger?.Warn($"[ToolHub] {endpoint.Method} {endpoint.Url} → HTTP {(int)resp.StatusCode}");
                return new ToolCallResult
                {
                    Success = false,
                    Error = $"HTTP {(int)resp.StatusCode}：{Truncate(body)}",
                    ElapsedMs = Elapsed(),
                };
            }

            var omitted = body.Length > MaxOutputChars;
            return new ToolCallResult
            {
                Success = true,
                Output = Truncate(body),
                IsOmitted = omitted,
                ElapsedMs = Elapsed(),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ToolHub] {endpoint.Method} {endpoint.Url} 调用失败：{ex.Message}");
            return new ToolCallResult { Success = false, Error = ex.Message, ElapsedMs = Elapsed() };
        }
    }

    private static string BuildUrl(string template, IReadOnlyDictionary<string, object?> args)
    {
        var url = template;
        foreach (var (key, value) in args)
            url = url.Replace("{" + key + "}", Uri.EscapeDataString(value?.ToString() ?? ""), StringComparison.Ordinal);
        return url;
    }

    private static string Truncate(string s)
        => s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + "\n...[截断]";

    public void Dispose() => _client.Dispose();
}
