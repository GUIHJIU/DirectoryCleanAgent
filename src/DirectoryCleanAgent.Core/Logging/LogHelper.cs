using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DirectoryCleanAgent.Core.Logging;

/// <summary>
/// 结构化日志辅助工具。
/// 封装 ILogger 的常用日志模式，自动附加调用方信息（方法名、文件路径、行号）。
/// </summary>
public static class LogHelper
{
    /// <summary>记录方法入口日志（Information 级别）</summary>
    public static void LogMethodEntry(
        this ILogger logger,
        string? message = null,
        [CallerMemberName] string methodName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        logger.LogInformation(
            "[入口] {Method} ({File}:{Line}) — {Message}",
            methodName, Path.GetFileName(filePath), lineNumber, message ?? "");
    }

    /// <summary>记录方法出口日志（Debug 级别，避免生产环境刷屏）</summary>
    public static void LogMethodExit(
        this ILogger logger,
        string? message = null,
        [CallerMemberName] string methodName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        logger.LogDebug(
            "[出口] {Method} ({File}:{Line}) — {Message}",
            methodName, Path.GetFileName(filePath), lineNumber, message ?? "");
    }

    /// <summary>记录错误日志并附加异常堆栈</summary>
    public static void LogError(
        this ILogger logger,
        Exception ex,
        string? message = null,
        [CallerMemberName] string methodName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        logger.LogError(ex,
            "[错误] {Method} ({File}:{Line}) — {Message}",
            methodName, Path.GetFileName(filePath), lineNumber, message ?? ex.Message);
    }

    /// <summary>记录警告日志</summary>
    public static void LogWarning(
        this ILogger logger,
        string message,
        [CallerMemberName] string methodName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        logger.LogWarning(
            "[警告] {Method} ({File}:{Line}) — {Message}",
            methodName, Path.GetFileName(filePath), lineNumber, message);
    }

    /// <summary>记录关键操作日志（Information 级别，用于审计追踪）</summary>
    public static void LogOperation(
        this ILogger logger,
        string operation,
        string target,
        string result,
        [CallerMemberName] string methodName = "")
    {
        logger.LogInformation(
            "[操作] {Operation} | 目标={Target} | 结果={Result} | 调用方={Method}",
            operation, target, result, methodName);
    }

    /// <summary>记录性能计时（Debug 级别）</summary>
    public static IDisposable? LogTiming(
        this ILogger logger,
        string operation,
        [CallerMemberName] string methodName = "")
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return null;

        logger.LogDebug("[计时开始] {Operation} @ {Method}", operation, methodName);
        return new TimingScope(logger, operation, methodName);
    }

    /// <summary>
    /// 内部类：using 块结束时自动计算耗时并输出日志。
    /// </summary>
    private sealed class TimingScope : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _operation;
        private readonly string _methodName;
        private readonly long _startTicks;

        public TimingScope(ILogger logger, string operation, string methodName)
        {
            _logger = logger;
            _operation = operation;
            _methodName = methodName;
            _startTicks = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTicks);
            _logger.LogDebug(
                "[计时结束] {Operation} @ {Method} — 耗时={Elapsed:F2}ms",
                _operation, _methodName, elapsed.TotalMilliseconds);
        }
    }
}
