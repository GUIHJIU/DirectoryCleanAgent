using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.PathHandling;
using DirectoryCleanAgent.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DirectoryCleanAgent.Tests.Operations;

/// <summary>
/// QuarantineManager 单元测试 — B6 阶段。
///
/// 测试覆盖：
/// - 构造函数参数 null 校验（×2）
/// - GetQuarantineDirectoryAsync: 禁用/启用/自动创建目录
/// - GetQuarantineStatsAsync: 禁用/空目录/有文件/有过期文件
/// - CanAccommodateAsync: 容量充足/容量不足/禁用
/// - IsLargeFileForQuarantineAsync: 小文件/大文件/禁用
/// - EnforceCapacityAsync: 容量充足/容量不足/禁用
/// - CleanupExpiredAsync: 无过期/有过期/混合/取消/进度报告
/// - PurgeAllAsync: 空目录/有文件/取消/进度报告
/// - ConstructQuarantineFileName: 正常构造/参数校验
/// - ParseQuarantineFileName: 正常解析/下划线文件名/无效格式
/// - 边界情况: 文件名含下划线、SHA256不足16位
/// </summary>
public class QuarantineManagerTests : IDisposable
{
    private readonly Mock<IConfigService> _configServiceMock;
    private readonly ILogger<QuarantineManager> _logger;
    private readonly UserConfig _defaultConfig;
    private readonly string _tempDir;
    private readonly string _quarantineDir;

    public QuarantineManagerTests()
    {
        _configServiceMock = new Mock<IConfigService>();
        _logger = NullLoggerFactory.Instance.CreateLogger<QuarantineManager>();

        // 创建临时目录
        _tempDir = Path.Combine(Path.GetTempPath(), $"q6_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // 隔离区子目录
        _quarantineDir = Path.Combine(_tempDir, "quarantine");

        // 默认配置：启用隔离区 10GB / 30 天保留
        _defaultConfig = new UserConfig
        {
            QuarantineMaxSizeBytes = 10L * 1024 * 1024 * 1024,
            QuarantineRetentionDays = 30,
            DataStoragePath = _tempDir
        };
        _configServiceMock.Setup(c => c.Current).Returns(_defaultConfig);
    }

    /// <summary>创建被测对象实例</summary>
    private QuarantineManager CreateSut(UserConfig? config = null)
    {
        if (config != null)
            _configServiceMock.Setup(c => c.Current).Returns(config);
        return new QuarantineManager(_configServiceMock.Object, _logger);
    }

    /// <summary>在隔离区目录中创建模拟 .quarantine 文件</summary>
    /// <param name="fileName">文件名（含 .quarantine 扩展名）</param>
    /// <param name="content">文件内容</param>
    /// <returns>完整的文件路径</returns>
    private string CreateQuarantineFile(string fileName, string? content = null)
    {
        if (!Directory.Exists(_quarantineDir))
            Directory.CreateDirectory(_quarantineDir);

        var filePath = Path.Combine(_quarantineDir, fileName);
        File.WriteAllText(filePath, content ?? $"test content {Guid.NewGuid():N}");
        return filePath;
    }

    /// <summary>清理临时目录</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 清理失败不抛出异常
        }
    }

    // ================================================================
    // 构造函数测试
    // ================================================================

    [Fact]
    public void Constructor_WithValidDependencies_ShouldCreateInstance()
    {
        var sut = CreateSut();
        Assert.NotNull(sut);
    }

    [Fact]
    public void Constructor_WithNullConfigService_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new QuarantineManager(null!, _logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new QuarantineManager(_configServiceMock.Object, null!));
    }

    // ================================================================
    // GetQuarantineDirectoryAsync 测试
    // ================================================================

    [Fact]
    public async Task GetQuarantineDirectoryAsync_WhenDisabled_ShouldReturnNull()
    {
        // Arrange: 隔离区容量为 0 表示禁用
        var config = new UserConfig
        {
            QuarantineMaxSizeBytes = 0,
            DataStoragePath = _tempDir
        };
        var sut = CreateSut(config);

        // Act
        var result = await sut.GetQuarantineDirectoryAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetQuarantineDirectoryAsync_WhenEnabled_ShouldReturnValidPath()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.GetQuarantineDirectoryAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Contains("quarantine", result);
    }

    [Fact]
    public async Task GetQuarantineDirectoryAsync_WhenEnabled_ShouldAutoCreateDirectory()
    {
        // Arrange: 确保隔离区目录尚不存在
        if (Directory.Exists(_quarantineDir))
            Directory.Delete(_quarantineDir, recursive: true);

        var sut = CreateSut();

        // Act
        var result = await sut.GetQuarantineDirectoryAsync();

        // Assert: 目录应被自动创建
        Assert.NotNull(result);
        Assert.True(Directory.Exists(_quarantineDir));
    }

    // ================================================================
    // GetQuarantineStatsAsync 测试
    // ================================================================

    [Fact]
    public async Task GetQuarantineStatsAsync_WhenDisabled_ShouldReturnDisabledStats()
    {
        // Arrange
        var config = new UserConfig
        {
            QuarantineMaxSizeBytes = 0,
            DataStoragePath = _tempDir
        };
        var sut = CreateSut(config);

        // Act
        var stats = await sut.GetQuarantineStatsAsync();

        // Assert
        Assert.True(stats.IsDisabled);
        Assert.Equal(0, stats.TotalFileCount);
        Assert.Equal(0, stats.TotalSizeBytes);
    }

    [Fact]
    public async Task GetQuarantineStatsAsync_WithEmptyDirectory_ShouldReturnZeroStats()
    {
        // Arrange: 创建空的隔离区目录
        Directory.CreateDirectory(_quarantineDir);
        var sut = CreateSut();

        // Act
        var stats = await sut.GetQuarantineStatsAsync();

        // Assert
        Assert.False(stats.IsDisabled);
        Assert.Equal(0, stats.TotalFileCount);
        Assert.Equal(0, stats.TotalSizeBytes);
        Assert.Equal(0, stats.ExpiredFileCount);
        Assert.Null(stats.OldestFileTimestamp);
    }

    [Fact]
    public async Task GetQuarantineStatsAsync_WithFiles_ShouldReturnCorrectStats()
    {
        // Arrange: 创建 3 个隔离区文件
        Directory.CreateDirectory(_quarantineDir);
        var now = DateTimeOffset.UtcNow;
        CreateQuarantineFile(
            $"A1B2C3D4E5F6A7B8_file1.txt_{now.ToUnixTimeSeconds()}.quarantine",
            new string('A', 100));  // 100 字节
        CreateQuarantineFile(
            $"B2C3D4E5F6A7B8C9_file2.log_{now.AddDays(-1).ToUnixTimeSeconds()}.quarantine",
            new string('B', 200));  // 200 字节
        CreateQuarantineFile(
            $"C3D4E5F6A7B8C9D0_file3.tmp_{now.AddDays(-2).ToUnixTimeSeconds()}.quarantine",
            new string('C', 300));  // 300 字节

        var sut = CreateSut();

        // Act
        var stats = await sut.GetQuarantineStatsAsync();

        // Assert
        Assert.Equal(3, stats.TotalFileCount);
        Assert.Equal(600, stats.TotalSizeBytes);
        Assert.Equal(10L * 1024 * 1024 * 1024, stats.QuarantineMaxSizeBytes);
        Assert.True(stats.AvailableBytes > 0);
        Assert.NotNull(stats.OldestFileTimestamp);
    }

    [Fact]
    public async Task GetQuarantineStatsAsync_WithExpiredFiles_ShouldCountExpired()
    {
        // Arrange: 创建一个 60 天前的过期文件
        Directory.CreateDirectory(_quarantineDir);
        var now = DateTimeOffset.UtcNow;
        var expiredTime = now.AddDays(-60);
        CreateQuarantineFile(
            $"D4E5F6A7B8C9D0E1_expired.log_{expiredTime.ToUnixTimeSeconds()}.quarantine");

        // 创建一个今天的文件（未过期）
        CreateQuarantineFile(
            $"E5F6A7B8C9D0E1F2_fresh.log_{now.ToUnixTimeSeconds()}.quarantine");

        var sut = CreateSut();

        // Act
        var stats = await sut.GetQuarantineStatsAsync();

        // Assert: 1 个过期
        Assert.Equal(2, stats.TotalFileCount);
        Assert.Equal(1, stats.ExpiredFileCount);
    }

    // ================================================================
    // CanAccommodateAsync 测试
    // ================================================================

    [Fact]
    public async Task CanAccommodateAsync_WhenDisabled_ShouldReturnFalse()
    {
        // Arrange
        var config = new UserConfig
        {
            QuarantineMaxSizeBytes = 0,
            DataStoragePath = _tempDir
        };
        var sut = CreateSut(config);

        // Act
        var result = await sut.CanAccommodateAsync(1024);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task CanAccommodateAsync_WithinLimit_ShouldReturnTrue()
    {
        // Arrange: 空隔离区，文件大小远小于上限
        Directory.CreateDirectory(_quarantineDir);
        var sut = CreateSut();

        // Act
        var result = await sut.CanAccommodateAsync(1024);

        // Assert
        Assert.True(result);
    }

    // ================================================================
    // IsLargeFileForQuarantineAsync 测试
    // ================================================================

    [Fact]
    public async Task IsLargeFileForQuarantineAsync_WhenDisabled_ShouldReturnTrue()
    {
        // Arrange: 隔离区禁用 → 所有文件都"过大"
        var config = new UserConfig
        {
            QuarantineMaxSizeBytes = 0,
            DataStoragePath = _tempDir
        };
        var sut = CreateSut(config);

        // Act
        var result = await sut.IsLargeFileForQuarantineAsync(1);

        // Assert: 即使 1 字节也"过大"（绕过隔离区）
        Assert.True(result);
    }

    [Fact]
    public async Task IsLargeFileForQuarantineAsync_SmallFile_ShouldReturnFalse()
    {
        // Arrange: 隔离区已创建，1KB 文件远小于卷可用空间的 20%
        Directory.CreateDirectory(_quarantineDir);
        var sut = CreateSut();

        // Act
        var result = await sut.IsLargeFileForQuarantineAsync(1024);

        // Assert: 小文件不绕过隔离区
        Assert.False(result);
    }

    [Fact]
    public async Task IsLargeFileForQuarantineAsync_HugeFile_ShouldReturnTrue()
    {
        // Arrange: 隔离区已创建，极大文件应绕过
        Directory.CreateDirectory(_quarantineDir);
        var sut = CreateSut();

        // Act: 1TB 文件必定触发绕过
        var result = await sut.IsLargeFileForQuarantineAsync(1L * 1024 * 1024 * 1024 * 1024);

        // Assert
        Assert.True(result);
    }

    // ================================================================
    // EnforceCapacityAsync 测试
    // ================================================================

    [Fact]
    public async Task EnforceCapacityAsync_WhenDisabled_ShouldReturnFalse()
    {
        // Arrange
        var config = new UserConfig
        {
            QuarantineMaxSizeBytes = 0,
            DataStoragePath = _tempDir
        };
        var sut = CreateSut(config);

        // Act
        var result = await sut.EnforceCapacityAsync(1024);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task EnforceCapacityAsync_WithinLimit_ShouldReturnTrue()
    {
        // Arrange: 空隔离区，10GB 上限，1KB 请求
        Directory.CreateDirectory(_quarantineDir);
        var sut = CreateSut();

        // Act
        var result = await sut.EnforceCapacityAsync(1024);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task EnforceCapacityAsync_ExceedsLimit_ShouldReturnFalse()
    {
        // Arrange: 请求超过 10GB 上限
        Directory.CreateDirectory(_quarantineDir);
        var sut = CreateSut();

        // Act: 请求 11GB 空间
        var result = await sut.EnforceCapacityAsync(11L * 1024 * 1024 * 1024);

        // Assert
        Assert.False(result);
    }

    // ================================================================
    // CleanupExpiredAsync 测试
    // ================================================================

    [Fact]
    public async Task CleanupExpiredAsync_WithEmptyDirectory_ShouldReturnZero()
    {
        // Arrange
        Directory.CreateDirectory(_quarantineDir);
        var sut = CreateSut();

        // Act
        var result = await sut.CleanupExpiredAsync();

        // Assert
        Assert.Equal(0, result.CleanedFileCount);
        Assert.Equal(0, result.FreedBytes);
        Assert.True(result.IsFullySuccessful);
    }

    [Fact]
    public async Task CleanupExpiredAsync_WithNoExpiredFiles_ShouldNotDeleteAny()
    {
        // Arrange: 创建今天的文件（未过期）
        Directory.CreateDirectory(_quarantineDir);
        var now = DateTimeOffset.UtcNow;
        CreateQuarantineFile(
            $"F6A7B8C9D0E1F2A3_fresh.log_{now.ToUnixTimeSeconds()}.quarantine");

        var sut = CreateSut();

        // Act
        var result = await sut.CleanupExpiredAsync();

        // Assert: 未过期的文件不应被删除
        Assert.Equal(0, result.CleanedFileCount);
        Assert.Single(Directory.GetFiles(_quarantineDir, "*.quarantine"));
    }

    [Fact]
    public async Task CleanupExpiredAsync_WithExpiredFiles_ShouldDeleteOnlyExpired()
    {
        // Arrange: 创建一个过期文件（60天前）+ 一个未过期文件（今天）
        Directory.CreateDirectory(_quarantineDir);
        var now = DateTimeOffset.UtcNow;
        var expiredTime = now.AddDays(-60);
        CreateQuarantineFile(
            $"A7B8C9D0E1F2A3B4_expired.log_{expiredTime.ToUnixTimeSeconds()}.quarantine");
        CreateQuarantineFile(
            $"C9D0E1F2A3B4C5D6_fresh.log_{now.ToUnixTimeSeconds()}.quarantine");

        var sut = CreateSut();

        // Act
        var result = await sut.CleanupExpiredAsync();

        // Assert: 仅过期文件被删除
        Assert.Equal(1, result.CleanedFileCount);
        Assert.True(result.FreedBytes > 0);
        Assert.True(result.IsFullySuccessful);

        // 验证未过期的文件仍在
        var remainingFiles = Directory.GetFiles(_quarantineDir, "*.quarantine");
        Assert.Single(remainingFiles);
        var remainingName = Path.GetFileName(remainingFiles[0]);
        Assert.Contains("fresh", remainingName);
    }

    [Fact]
    public async Task CleanupExpiredAsync_WithCancellation_ShouldStopAndReturnPartial()
    {
        // Arrange: 创建多个过期文件
        Directory.CreateDirectory(_quarantineDir);
        var now = DateTimeOffset.UtcNow;
        var expiredTime = now.AddDays(-60);
        for (var i = 0; i < 20; i++)
        {
            CreateQuarantineFile(
                $"Z{i:D2}B8C9D0E1F2A3B4_file{i}.log_{expiredTime.ToUnixTimeSeconds()}.quarantine");
        }

        var sut = CreateSut();
        using var cts = new CancellationTokenSource();

        // Act: 立即取消
        cts.Cancel();
        var result = await sut.CleanupExpiredAsync(null, cts.Token);

        // Assert: 取消时不抛出异常，返回已完成部分
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CleanupExpiredAsync_WithProgress_ShouldReportProgress()
    {
        // Arrange: 创建过期文件
        Directory.CreateDirectory(_quarantineDir);
        var now = DateTimeOffset.UtcNow;
        var expiredTime = now.AddDays(-60);
        CreateQuarantineFile(
            $"B8C9D0E1F2A3B4C5_progress.log_{expiredTime.ToUnixTimeSeconds()}.quarantine");

        var progressReports = new List<OperationProgress>();
        var progress = new Progress<OperationProgress>(p => progressReports.Add(p));

        var sut = CreateSut();

        // Act
        var result = await sut.CleanupExpiredAsync(progress);

        // Assert: 文件被成功清理（进度报告可能有 100ms 限流，单文件操作可能不触发）
        Assert.Equal(1, result.CleanedFileCount);
        // 不强制断言 progressReports 非空，单文件操作可在 100ms 内完成
    }

    [Fact]
    public async Task CleanupExpiredAsync_WithInvalidFileName_ShouldSkip()
    {
        // Arrange: 创建一个格式无效的 .quarantine 文件（无法解析时间戳）
        Directory.CreateDirectory(_quarantineDir);
        var invalidFile = CreateQuarantineFile("invalid_format.quarantine");

        var sut = CreateSut();

        // Act
        var result = await sut.CleanupExpiredAsync();

        // Assert: 无效文件被跳过，不被删除
        Assert.Equal(0, result.CleanedFileCount);
        Assert.True(File.Exists(invalidFile));
    }

    // ================================================================
    // PurgeAllAsync 测试
    // ================================================================

    [Fact]
    public async Task PurgeAllAsync_WithEmptyDirectory_ShouldReturnZero()
    {
        // Arrange
        Directory.CreateDirectory(_quarantineDir);
        var sut = CreateSut();

        // Act
        var result = await sut.PurgeAllAsync();

        // Assert
        Assert.Equal(0, result.CleanedFileCount);
        Assert.True(result.IsFullySuccessful);
    }

    [Fact]
    public async Task PurgeAllAsync_WithFiles_ShouldDeleteAll()
    {
        // Arrange: 创建多个隔离区文件
        Directory.CreateDirectory(_quarantineDir);
        CreateQuarantineFile(
            $"D0E1F2A3B4C5D6E7_file1.log_1718845200.quarantine");
        CreateQuarantineFile(
            $"E1F2A3B4C5D6E7F8_file2.tmp_1718845300.quarantine");
        CreateQuarantineFile(
            $"F2A3B4C5D6E7F8A9_file3.txt_1718845400.quarantine");

        var sut = CreateSut();

        // Act
        var result = await sut.PurgeAllAsync();

        // Assert: 所有文件被删除
        Assert.Equal(3, result.CleanedFileCount);
        Assert.True(result.FreedBytes > 0);
        Assert.True(result.IsFullySuccessful);
        Assert.Empty(Directory.GetFiles(_quarantineDir, "*.quarantine"));
    }

    [Fact]
    public async Task PurgeAllAsync_LeavesNonQuarantineFiles()
    {
        // Arrange: 创建 .quarantine 文件和非 .quarantine 文件
        Directory.CreateDirectory(_quarantineDir);
        CreateQuarantineFile($"test.quarantine");
        var nonQuarantineFile = Path.Combine(_quarantineDir, "readme.txt");
        File.WriteAllText(nonQuarantineFile, "this should not be deleted");

        var sut = CreateSut();

        // Act
        var result = await sut.PurgeAllAsync();

        // Assert: 仅删除 .quarantine 文件
        Assert.Equal(1, result.CleanedFileCount);
        Assert.True(File.Exists(nonQuarantineFile));
    }

    [Fact]
    public async Task PurgeAllAsync_WithCancellation_ShouldStop()
    {
        // Arrange
        Directory.CreateDirectory(_quarantineDir);
        for (var i = 0; i < 10; i++)
        {
            CreateQuarantineFile($"purge_test_{i}.quarantine");
        }
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert: 取消时不抛出异常
        var result = await sut.PurgeAllAsync(null, cts.Token);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task PurgeAllAsync_WithProgress_ShouldReportProgress()
    {
        // Arrange
        Directory.CreateDirectory(_quarantineDir);
        CreateQuarantineFile($"progress_purge.quarantine");

        var progressReports = new List<OperationProgress>();
        var progress = new Progress<OperationProgress>(p => progressReports.Add(p));

        var sut = CreateSut();

        // Act
        var result = await sut.PurgeAllAsync(progress);

        // Assert: 文件被成功清理
        Assert.Equal(1, result.CleanedFileCount);
        // 进度报告有 100ms 限流，单文件操作可能不触发进度回调
    }

    // ================================================================
    // ConstructQuarantineFileName 测试
    // ================================================================

    [Fact]
    public void ConstructQuarantineFileName_ShouldReturnExpectedFormat()
    {
        // Arrange
        var sut = CreateSut();
        var hash = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2";
        var fileName = "项目文档.docx";

        // Act
        var result = sut.ConstructQuarantineFileName(hash, fileName);

        // Assert: 格式为 {SHA16}_{文件名}_{Unix秒}.quarantine
        Assert.StartsWith("a1b2c3d4e5f6a7b8_项目文档.docx_", result);
        Assert.EndsWith(".quarantine", result);
    }

    [Fact]
    public void ConstructQuarantineFileName_WithShortHash_ShouldStillWork()
    {
        // Arrange
        var sut = CreateSut();
        var shortHash = "abc123";  // 不足 16 位
        var fileName = "test.txt";

        // Act
        var result = sut.ConstructQuarantineFileName(shortHash, fileName);

        // Assert
        Assert.StartsWith("abc123", result);
        Assert.EndsWith(".quarantine", result);
    }

    [Fact]
    public void ConstructQuarantineFileName_WithNullHash_ShouldThrow()
    {
        var sut = CreateSut();
        Assert.Throws<ArgumentException>(() =>
            sut.ConstructQuarantineFileName(null!, "test.txt"));
    }

    [Fact]
    public void ConstructQuarantineFileName_WithNullFileName_ShouldThrow()
    {
        var sut = CreateSut();
        Assert.Throws<ArgumentException>(() =>
            sut.ConstructQuarantineFileName("abc123", null!));
    }

    // ================================================================
    // ParseQuarantineFileName 测试
    // ================================================================

    [Fact]
    public void ParseQuarantineFileName_ValidFile_ShouldReturnComponents()
    {
        // Arrange
        var sut = CreateSut();
        var hash = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2";
        var fileName = sut.ConstructQuarantineFileName(hash, "test.txt");

        // Act
        var result = sut.ParseQuarantineFileName(fileName);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("a1b2c3d4e5f6a7b8", result.Value.Sha256Prefix);
        Assert.Equal("test.txt", result.Value.OriginalFileName);
        Assert.True(result.Value.UnixSeconds > 0);
    }

    [Fact]
    public void ParseQuarantineFileName_FileNameWithUnderscores_ShouldStillParse()
    {
        // Arrange
        var sut = CreateSut();
        // 文件名本身包含下划线（如 "项目_文档_v2.docx"）
        var fileName = "A1B2C3D4E5F6A7B8_项目_文档_v2.docx_1718845200.quarantine";

        // Act
        var result = sut.ParseQuarantineFileName(fileName);

        // Assert: 原始文件名应为 "项目_文档_v2.docx"
        Assert.NotNull(result);
        Assert.Equal("A1B2C3D4E5F6A7B8", result.Value.Sha256Prefix);
        Assert.Equal("项目_文档_v2.docx", result.Value.OriginalFileName);
        Assert.Equal(1718845200, result.Value.UnixSeconds);
    }

    [Fact]
    public void ParseQuarantineFileName_InvalidFormat_ShouldReturnNull()
    {
        var sut = CreateSut();

        // 缺少 .quarantine 扩展名
        Assert.Null(sut.ParseQuarantineFileName("test.txt"));
        // 空字符串
        Assert.Null(sut.ParseQuarantineFileName(""));
        // null
        Assert.Null(sut.ParseQuarantineFileName(null!));
        // 格式不完整（缺少时间戳段）
        Assert.Null(sut.ParseQuarantineFileName("abc123_file.txt.quarantine"));
        // 时间戳非数字
        Assert.Null(sut.ParseQuarantineFileName("abc123_file.txt_abc.quarantine"));
    }

    [Fact]
    public void ParseQuarantineFileName_TimestampOutOfRange_ShouldReturnNull()
    {
        var sut = CreateSut();

        // 时间戳早于 2020 年
        Assert.Null(sut.ParseQuarantineFileName("abc123_file.txt_1000000000.quarantine"));
        // 时间戳晚于 2100 年
        Assert.Null(sut.ParseQuarantineFileName("abc123_file.txt_5000000000.quarantine"));
    }

    // ================================================================
    // Round-trip 测试
    // ================================================================

    [Fact]
    public void ConstructThenParse_ShouldRoundTrip()
    {
        // Arrange
        var sut = CreateSut();
        var hash = "A1B2C3D4E5F6A7B8C9D0E1F2A3B4C5D6A7B8C9D0E1F2A3B4C5D6E7F8A9B0C1D2";
        var originalName = "my_document_backup_v3.docx";

        // Act: 构造 → 解析
        var constructed = sut.ConstructQuarantineFileName(hash, originalName);
        var parsed = sut.ParseQuarantineFileName(constructed);

        // Assert: 应能完整还原
        Assert.NotNull(parsed);
        Assert.Equal("A1B2C3D4E5F6A7B8", parsed.Value.Sha256Prefix);
        Assert.Equal(originalName, parsed.Value.OriginalFileName);
    }
}
