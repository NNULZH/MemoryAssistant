using MemoryAssistant.Core.Agent;

namespace MemoryAssistant.Tests;

/// <summary>
/// 可编程 mock：按脚本队列返回 ChatResult。
/// 用于验证 AgentLoop 的调度行为，不依赖真实 LLM。
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    public ScriptedChatClient(IReadOnlyList<ChatResult> script)
    {
        _script = new Queue<ChatResult>(script);
        Calls = [];
    }

    private readonly Queue<ChatResult> _script;
    public List<(int MessageCount, int ToolCount)> Calls { get; }
    /// <summary>可选钩子：每轮调用时拿到完整消息列表（用于断言回填）。</summary>
    public Action<IReadOnlyList<ChatMessage>>? CallsHooks { get; set; }
    public string ModelName => "mock";

    public Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSchema>? tools = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add((messages.Count, tools?.Count ?? 0));
        CallsHooks?.Invoke(messages);
        var result = _script.Count > 0 ? _script.Dequeue() : new ChatResult { Content = "fallback" };
        return Task.FromResult(result);
    }
}
