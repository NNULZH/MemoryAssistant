using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Infrastructure.Logging;

/// <summary>控制台日志实现。日志不记录聊天原文（隐私约束）。</summary>
public sealed class ConsoleLogger : IAppLogger
{
    public ConsoleLogger()
    {
        // 中文输出：强制控制台使用 UTF-8，避免 Windows 默认代码页导致乱码。
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
        catch { /* 某些宿主不可改，忽略 */ }
    }

    public void Log(LogLevel level, string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var color = level switch
        {
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Info => ConsoleColor.White,
            LogLevel.Warn => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            _ => ConsoleColor.White
        };
        lock (SyncRoot)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"[{ts}][{level.ToString().ToUpper()}] {message}");
            Console.ResetColor();
        }
    }

    private static readonly object SyncRoot = new();
}