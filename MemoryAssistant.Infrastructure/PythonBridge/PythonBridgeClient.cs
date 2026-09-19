using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.PythonBridge;

namespace MemoryAssistant.Infrastructure.PythonBridge;

/// <summary>
/// 通过 stdin/stdout + JSON Lines 与 bridge.py 通信。
/// 规则：
///  1. 每条请求带唯一 id；
///  2. 响应通过 id 关联请求；
///  3. Python 异常不会导致 C# 崩溃（以 success=false 返回）；
///  4. C# 侧处理超时；
///  5. 进程退出后可自动重启；
///  6. stdout 保持纯 JSON Lines。
/// </summary>
public sealed class PythonBridgeClient : IPythonBridge, IDisposable
{
    public PythonBridgeClient(
        string pythonExePath,
        string bridgeScriptPath,
        IAppLogger logger,
        int idleTimeoutSeconds = 30)
    {
        _pythonExePath = pythonExePath;
        _bridgeScriptPath = bridgeScriptPath;
        _logger = logger;
        _idleTimeoutSeconds = idleTimeoutSeconds;
    }

    private readonly string _pythonExePath;
    private readonly string _bridgeScriptPath;
    private readonly IAppLogger _logger;
    private readonly int _idleTimeoutSeconds;

    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> _pending = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _readLoop;
    private TaskCompletionSource<bool>? _readyTcs;

    public bool IsRunning => _process is { HasExited: false };

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            if (IsRunning) return true;

            var psi = new ProcessStartInfo
            {
                FileName = _pythonExePath,
                Arguments = $"\"{_bridgeScriptPath}\" --preload",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUNBUFFERED"] = "1";

            _process = new Process { StartInfo = psi };
            if (!_process.Start())
            {
                _logger.Error("Python Bridge 进程启动失败。");
                return false;
            }

            _stdin = _process.StandardInput;
            _readLoop = Task.Run(() => ReadLoopAsync(_process));
            _ = Task.Run(() => StderrLoopAsync(_process));

            // 等待预热就绪标记（--preload 下 SDK 初始化可能较慢）。
            var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _readyTcs = readyTcs;
            var readyDone = await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(120))).ConfigureAwait(false);
            _readyTcs = null;
            if (readyDone != readyTcs.Task)
            {
                _logger.Warn("Python Bridge 预热 120s 未就绪，继续启动（请求可能超时）。");
            }

            _logger.Info($"Python Bridge 已启动 (pid={_process.Id})。");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Python Bridge 启动异常: {ex.Message}");
            return false;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"停止 Python Bridge 时异常: {ex.Message}");
        }
        _process = null;
        _stdin = null;
        _logger.Info("Python Bridge 已停止。");
    }

    public async Task<BridgeResponse> RequestAsync(
        string method,
        IReadOnlyDictionary<string, object?>? args = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        await EnsureRunningAsync(ct);
        var id = Guid.NewGuid().ToString("N");
        var request = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = args ?? new Dictionary<string, object?>()
        };
        var json = JsonSerializer.Serialize(request);

        var tcs = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _stdin!.WriteLineAsync(json.AsMemory(), ct);
            await _stdin.FlushAsync(ct);

            var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? _idleTimeoutSeconds);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (done != tcs.Task)
            {
                _logger.Warn($"Bridge 请求 {method} 超时 ({timeout.TotalSeconds:0}s)。");
                return new BridgeResponse(id, false, null, $"timeout after {timeout.TotalSeconds:0}s");
            }
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new BridgeResponse(id, false, null, "cancelled");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bridge 请求 {method} 写入失败: {ex.Message}");
            return new BridgeResponse(id, false, null, ex.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task EnsureRunningAsync(CancellationToken ct)
    {
        if (IsRunning) return;
        var ok = await StartAsync(ct);
        if (!ok) throw new InvalidOperationException("Python Bridge 不可用。");
    }

    private async Task ReadLoopAsync(Process proc)
    {
        try
        {
            while (!proc.HasExited && proc.StandardOutput is { } so)
            {
                var line = await so.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                BridgeResponse? resp;
                try
                {
                    var obj = JsonSerializer.Deserialize<JsonElement>(line);

                    // 就绪标记（预热完成信号），不放 pending。
                    if (obj.TryGetProperty("event", out var evt) && evt.GetString() == "ready")
                    {
                        _readyTcs?.TrySetResult(true);
                        continue;
                    }

                    var id = obj.GetProperty("id").GetString() ?? "";
                    var success = obj.TryGetProperty("success", out var s) && s.GetBoolean();
                    object? data = null;
                    if (obj.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null)
                        data = d;
                    string? err = obj.TryGetProperty("error", out var e) ? e.GetString() : null;
                    resp = new BridgeResponse(id, success, data, err);
                }
                catch (JsonException ex)
                {
                    _logger.Warn($"Bridge 返回非法 JSON: {ex.Message}");
                    continue;
                }

                if (resp is null) continue;
                if (_pending.TryRemove(resp.Id, out var tcs))
                {
                    tcs.TrySetResult(resp);
                }
                else
                {
                    // 无对应请求的响应：可能是事件推送，第一版忽略。
                    _logger.Debug($"Bridge 收到未知 id 响应: {resp.Id}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Bridge 读取循环异常: {ex.Message}");
        }
        finally
        {
            // 进程退出，把所有挂起请求标记失败。
            foreach (var kv in _pending)
            {
                if (_pending.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetResult(new BridgeResponse(kv.Key, false, null, "bridge process exited"));
            }
        }
    }

    private async Task StderrLoopAsync(Process proc)
    {
        try
        {
            while (!proc.HasExited && proc.StandardError is { } se)
            {
                var line = await se.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line))
                    _logger.Warn($"[Bridge-stderr] {line}");
            }
        }
        catch
        {
            // 忽略：读取循环关闭是正常退出。
        }
    }

    public void Dispose()
    {
        if (IsRunning)
        {
            try
            {
                _process!.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
        }
        _pending.Clear();
        _startLock.Dispose();
    }
}