// ============================================================
// IntegrationTestBase.cs
// 功能: 集成测试基类
// 提供统一的临时目录管理、日志工厂、Moq 配置服务、文件辅助方法
// 所有集成测试可选继承此类以减少重复的 SetUp/TearDown 代码
// ============================================================

using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DirectoryCleanAgent.Tests.Infrastructure;

/// <summary>
/// 集成测试抽象基类
/// 提供:
///   1. 测试级临时目录（自动创建/清理）
///   2. ILoggerFactory（NullLogger — 测试中不产生实际日志输出）
///   3. 预配置的 Mock<IConfigService>
///   4. 文件创建辅助方法
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    /// <summary>测试级临时根目录（每个测试实例独立）</summary>
    protected string TestRoot { get; }

    /// <summary>日志工厂（默认 NullLogger）</summary>
    protected ILoggerFactory LoggerFactory { get; }

    /// <summary>预配置的配置服务 Mock</summary>
    protected Mock<IConfigService> ConfigServiceMock { get; }

    /// <summary>默认测试配置</summary>
    protected UserConfig DefaultConfig { get; private set; }

    private bool _disposed;

    protected IntegrationTestBase(string? testPrefix = null)
    {
        // 创建测试级临时目录
        testPrefix ??= GetType().Name.ToLowerInvariant();
        TestRoot = Path.Combine(Path.GetTempPath(), $"{testPrefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TestRoot);

        // 创建日志工厂
        LoggerFactory = NullLoggerFactory.Instance;

        // 创建默认配置
        DefaultConfig = TestConfigBuilder.CreateDefault(TestRoot);

        // 创建配置服务 Mock
        ConfigServiceMock = new Mock<IConfigService>();
        ConfigServiceMock.Setup(c => c.Current).Returns(DefaultConfig);
    }

    // ============================================================
    // 文件辅助方法
    // ============================================================

    /// <summary>
    /// 在测试根目录下创建测试文件
    /// </summary>
    /// <param name="relativePath">相对于测试根目录的路径</param>
    /// <param name="content">文件内容（可选，默认生成随机内容）</param>
    /// <param name="sizeBytes">文件大小（当 content 为 null 时使用）</param>
    /// <returns>文件完整路径（\\?\ 格式）</returns>
    protected string CreateTestFile(string relativePath, byte[]? content = null, long sizeBytes = 1024)
    {
        string fullPath = Path.Combine(TestRoot, relativePath);
        // 规范化路径分隔符，确保所有分隔符统一为 \
        fullPath = Path.GetFullPath(fullPath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (content != null)
        {
            File.WriteAllBytes(fullPath, content);
        }
        else
        {
            byte[] data = new byte[sizeBytes];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(fullPath, data);
        }

        return fullPath;
    }

    /// <summary>
    /// 在测试根目录下创建测试目录
    /// </summary>
    protected string CreateTestDirectory(string relativePath)
    {
        string fullPath = Path.Combine(TestRoot, relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    /// <summary>
    /// 计算文件的 SHA-256 哈希值（小写十六进制）
    /// </summary>
    protected static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ============================================================
    // Moq 辅助方法
    // ============================================================

    /// <summary>创建带 NullLogger 的 ILogger<T> Mock</summary>
    protected ILogger<T> CreateLogger<T>() where T : class
    {
        return NullLoggerFactory.Instance.CreateLogger<T>();
    }

    /// <summary>更新 Mock 中的 UserConfig（模拟配置变更）</summary>
    protected void UpdateConfig(UserConfig newConfig)
    {
        DefaultConfig = newConfig;
        ConfigServiceMock.Setup(c => c.Current).Returns(newConfig);
        ConfigServiceMock.Raise(c => c.ConfigChanged += null, EventArgs.Empty);
    }

    // ============================================================
    // 数据库辅助方法
    // ============================================================

    /// <summary>
    /// 创建临时 SQLite 测试数据库并初始化完整表结构（幂等）。
    /// 返回 (数据库路径, ISqliteConnectionFactory) 供集成测试使用。
    /// 数据库文件存放在 TestRoot 下，随测试实例销毁自动清理。
    /// </summary>
    /// <param name="dbFileName">数据库文件名（可选，默认为 test_integration.db）</param>
    /// <returns>(数据库完整路径, ISqliteConnectionFactory 实例)</returns>
    protected async Task<(string DbPath, ISqliteConnectionFactory ConnectionFactory)> CreateTestDbAsync(string? dbFileName = null)
    {
        var dbPath = Path.Combine(TestRoot, dbFileName ?? "test_integration.db");
        var logger = NullLoggerFactory.Instance.CreateLogger<DatabaseInitializer>();
        var initializer = new DatabaseInitializer(dbPath, logger);

        // 初始化数据库（建表 + WAL + 索引，幂等操作）
        await initializer.InitializeAsync();

        return (dbPath, initializer);
    }

    // ============================================================
    // 清理
    // ============================================================

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (Directory.Exists(TestRoot))
            {
                Directory.Delete(TestRoot, true);
            }
        }
        catch
        {
            // 某些文件可能被占用（如 SQLite WAL 文件），清理失败不抛异常
            // Temp 目录由操作系统定期清理
        }
    }
}
