namespace MemoryAssistant.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>应用日志抽象，避免业务层耦合具体实现。</summary>
public interface IAppLogger
{
    void Log(LogLevel level, string message);
    void Debug(string message) => Log(LogLevel.Debug, message);
    void Info(string message) => Log(LogLevel.Info, message);
    void Warn(string message) => Log(LogLevel.Warn, message);
    void Error(string message) => Log(LogLevel.Error, message);
}