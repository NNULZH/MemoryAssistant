namespace MemoryAssistant.Core.Integrations;

/// <summary>一个候选窗口（只读枚举结果）。</summary>
public sealed record WindowInfo(string Title, string ClassName, int ProcessId, bool IsVisible)
{
    public override string ToString() => $"{Title}（{ClassName}, pid={ProcessId}{(IsVisible ? "" : ", 最小化/隐藏")}）";
}

/// <summary>读取"当前可见聊天"的结果。</summary>
public sealed record VisibleChatRead
{
    public bool Success { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<string> Messages { get; init; } = [];
    public string? Error { get; init; }

    /// <summary>读取通道（UIA / OCR/PrintWindow / OCR/ScreenCapture），用于验收与排障。</summary>
    public string Method { get; init; } = "";
}

/// <summary>
/// 微信桌面窗口只读桥（V3.3）：找窗口 + 读当前可见聊天。
/// 只读、不点击、不发送；写操作（打开会话/发送）留给 V3.4 且必须人工确认。
/// 实现在 App（Windows UIAutomation），Core 只依赖抽象，便于单测与替换。
/// </summary>
public interface IWeChatWindowBridge
{
    /// <summary>当前环境是否具备读取能力（能找到微信窗口）。</summary>
    bool IsAvailable { get; }

    Task<IReadOnlyList<WindowInfo>> ListWeChatWindowsAsync(CancellationToken ct = default);

    /// <summary>读取当前前台/可见聊天窗口中的消息文本（倒序或正序由实现决定，仅文本）。</summary>
    Task<VisibleChatRead> ReadVisibleChatAsync(CancellationToken ct = default);
}
