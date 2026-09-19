using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MemoryAssistant.App.Acceptance;

/// <summary>
/// 验收用的最小本地 HTTP 服务（手写 TcpListener + HTTP/1.1 响应）。
/// 用 TcpListener 而不是 HttpListener：后者在 Windows 上需要 URL 保留/管理员权限，
/// 而这个服务只是给"端到端工具调用验收"提供一个能被真实 LLM 工具链打到的外部端点。
/// 会记录每一次请求（方法 + 路径），用来证明"模型确实调用了外部工具"。
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, string, (string ContentType, string Body)> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _hits = [];

    public LocalHttpServer(Func<string, string, (string ContentType, string Body)> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);   // 0 = 自动挑空闲端口
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl { get; }

    public int HitCount
    {
        get { lock (_hits) return _hits.Count; }
    }

    public IReadOnlyList<string> Hits
    {
        get { lock (_hits) return _hits.ToList(); }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                break;   // 停止/取消
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync() ?? "";
                string? headerLine;
                do { headerLine = await reader.ReadLineAsync(); } while (!string.IsNullOrEmpty(headerLine));

                var parts = requestLine.Split(' ');
                var method = parts.Length > 0 ? parts[0] : "GET";
                var path = parts.Length > 1 ? parts[1] : "/";
                lock (_hits) _hits.Add($"{method} {path}");

                var (contentType, body) = _handler(method, path);
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}; charset=utf-8\r\n" +
                           $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            catch
            {
                // 验收用的临时服务：单个请求失败不影响整体
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* 已停止 */ }
        _cts.Dispose();
    }
}
