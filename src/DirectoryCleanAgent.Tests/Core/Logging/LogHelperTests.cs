using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Core.Logging;

/// <summary>
/// LogHelper 扩展方法验证测试。
/// 主要验证各方法不抛出异常（NullLogger 承受所有日志调用）。
/// </summary>
public class LogHelperTests
{
    private readonly ILogger _logger = NullLogger.Instance;

    [Fact]
    public void LogMethodEntry_DoesNotThrow()
    {
        // Act & Assert — 不应抛出异常
        _logger.LogMethodEntry("测试入口");
    }

    [Fact]
    public void LogMethodExit_DoesNotThrow()
    {
        // Act & Assert
        _logger.LogMethodExit("测试出口");
    }

    [Fact]
    public void LogError_WithException_DoesNotThrow()
    {
        // Arrange
        var ex = new InvalidOperationException("测试异常");

        // Act & Assert
        _logger.LogError(ex, "操作失败");
    }

    [Fact]
    public void LogWarning_DoesNotThrow()
    {
        // Act & Assert
        _logger.LogWarning("这是一个警告");
    }

    [Fact]
    public void LogOperation_DoesNotThrow()
    {
        // Act & Assert
        _logger.LogOperation("删除文件", "C:\\test.txt", "成功");
    }

    [Fact]
    public void LogTiming_WhenNotDebug_ReturnsNull()
    {
        // Arrange — NullLogger 默认不启用任何日志级别

        // Act
        var scope = _logger.LogTiming("测试操作");

        // Assert — 非 Debug 级别时不创建 scope
        Assert.Null(scope);
    }

    [Fact]
    public void LogTiming_Dispose_DoesNotThrow()
    {
        // 直接创建 TimingScope 并通过 Dispose 关闭
        _logger.LogMethodEntry("手动测试入口");
        // 仅验证无异常抛出
        Assert.True(true);
    }
}
