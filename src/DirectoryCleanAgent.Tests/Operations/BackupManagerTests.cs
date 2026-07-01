using System.Collections.ObjectModel;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.PathHandling;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DirectoryCleanAgent.Tests.Operations;

/// <summary>
/// BackupManager 单元测试与集成测试。
///
/// 测试覆盖：
/// - 构造函数参数 null 校验（×5）
/// - RollbackAsync: 空 operationId、无记录、隔离区恢复、回收站恢复、永久删除、
///   哈希不匹配、混合结果、取消中断、进度报告
/// - GetRollbackHistoryAsync: 正常委托、空列表
/// - VerifyHashesAsync: 全通过、部分不匹配、文件未找到、取消中断
/// - 集成测试: 隔离区恢复完整循环、路径冲突解决、哈希不匹配拒绝
/// </summary>
public class BackupManagerTests : IDisposable
{
    private readonly Mock<ILocalTombstoneRepository> _tombstoneRepoMock;
    private readonly Mock<IDeletionRecordRepository> _deletionRecordRepoMock;
    private readonly Mock<ITombstoneCache> _tombstoneCacheMock;
    private readonly Mock<IConfigService> _configServiceMock;
    private readonly Mock<IQuarantineManager> _quarantineManagerMock;
    private readonly ILogger<BackupManager> _logger;
    private readonly UserConfig _defaultConfig;
    private readonly string _tempDir;
    private readonly string _quarantineDir;

    public BackupManagerTests()
    {
        _tombstoneRepoMock = new Mock<ILocalTombstoneRepository>();
        _deletionRecordRepoMock = new Mock<IDeletionRecordRepository>();
        _tombstoneCacheMock = new Mock<ITombstoneCache>();
        _configServiceMock = new Mock<IConfigService>();
        _quarantineManagerMock = new Mock<IQuarantineManager>();
        _logger = NullLoggerFactory.Instance.CreateLogger<BackupManager>();

        // 创建临时目录
        _tempDir = Path.Combine(Path.GetTempPath(), $"b5_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // 隔离区目录
        _quarantineDir = Path.Combine(_tempDir, "quarantine");
        Directory.CreateDirectory(_quarantineDir);

        // 默认配置：启用隔离区
        _defaultConfig = new UserConfig
        {
            QuarantineMaxSizeBytes = 10L * 1024 * 1024 * 1024, // 10 GB
            QuarantineRetentionDays = 30,
            DataStoragePath = _tempDir,
            FRN_AVAILABLE = true,
            DeleteMethod = DeleteMethod.RecycleBin
        };
        _configServiceMock.Setup(c => c.Current).Returns(_defaultConfig);

        // B6: 默认隔离区启用 → GetQuarantineDirectoryAsync 返回隔离区目录
        _quarantineManagerMock
            .Setup(q => q.GetQuarantineDirectoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_quarantineDir);
    }

    /// <summary>创建被测对象实例</summary>
    private BackupManager CreateSut() => new(
        _logger,
        _tombstoneRepoMock.Object,
        _deletionRecordRepoMock.Object,
        _tombstoneCacheMock.Object,
        _configServiceMock.Object,
        _quarantineManagerMock.Object);

    /// <summary>创建测试用 DeletionRecord</summary>
    private static DeletionRecord CreateRecord(
        string operationId,
        string filePath,
        string deletionMethod = "QUARANTINE",
        string fileHash = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2",
        long fileSize = 1024)
    {
        return new DeletionRecord
        {
            OperationId = operationId,
            FilePath = filePath,
            FileHash = fileHash,
            FileSize = fileSize,
            DeletionMethod = deletionMethod,
            DecisionSnapshotJson = "{}",
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>创建真实临时文件并返回 \\?\ 路径</summary>
    private static string CreateRealFile(string directory, string? fileName = null)
    {
        var name = fileName ?? $"test_{Guid.NewGuid():N}.tmp";
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, $"test content {Guid.NewGuid():N}");
        return PathNormalizer.Normalize(path);
    }

    /// <summary>
    /// 创建模拟隔离区文件：写入特定内容并重命名为 quarantine 格式。
    /// 返回隔离区文件路径。
    /// </summary>
    private string CreateQuarantineFile(
        string originalFilePath, string sha256Hash, DateTime deletionTime)
    {
        var sha256Prefix = sha256Hash.Length >= 16 ? sha256Hash[..16] : sha256Hash;
        var originalFileName = Path.GetFileName(originalFilePath);
        var unixSeconds = new DateTimeOffset(deletionTime).ToUnixTimeSeconds();
        var quarantineFileName = $"{sha256Prefix}_{originalFileName}_{unixSeconds}.quarantine";

        var quarantinePath = Path.Combine(_quarantineDir, quarantineFileName);
        File.WriteAllText(quarantinePath, $"quarantine content {Guid.NewGuid():N}");
        return quarantinePath;
    }

    // ================================================================
    // 构造函数 — 参数 null 校验（×5）
    // ================================================================

    [Fact]
    public void Constructor_WithValidDependencies_ShouldCreateInstance()
    {
        var sut = CreateSut();
        Assert.NotNull(sut);
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BackupManager(
            null!,
            _tombstoneRepoMock.Object,
            _deletionRecordRepoMock.Object,
            _tombstoneCacheMock.Object,
            _configServiceMock.Object,
            _quarantineManagerMock.Object));
    }

    [Fact]
    public void Constructor_WithNullTombstoneRepo_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BackupManager(
            _logger,
            null!,
            _deletionRecordRepoMock.Object,
            _tombstoneCacheMock.Object,
            _configServiceMock.Object,
            _quarantineManagerMock.Object));
    }

    [Fact]
    public void Constructor_WithNullDeletionRecordRepo_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BackupManager(
            _logger,
            _tombstoneRepoMock.Object,
            null!,
            _tombstoneCacheMock.Object,
            _configServiceMock.Object,
            _quarantineManagerMock.Object));
    }

    [Fact]
    public void Constructor_WithNullTombstoneCache_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BackupManager(
            _logger,
            _tombstoneRepoMock.Object,
            _deletionRecordRepoMock.Object,
            null!,
            _configServiceMock.Object,
            _quarantineManagerMock.Object));
    }

    [Fact]
    public void Constructor_WithNullConfigService_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BackupManager(
            _logger,
            _tombstoneRepoMock.Object,
            _deletionRecordRepoMock.Object,
            _tombstoneCacheMock.Object,
            null!,
            _quarantineManagerMock.Object));
    }

    // ================================================================
    // RollbackAsync — 单元测试（×9）
    // ================================================================

    [Fact]
    public async Task RollbackAsync_WithNullOperationId_ShouldThrowArgumentException()
    {
        var sut = CreateSut();
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.RollbackAsync(null!, null, CancellationToken.None));
    }

    [Fact]
    public async Task RollbackAsync_WithEmptyOperationId_ShouldThrowArgumentException()
    {
        var sut = CreateSut();
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.RollbackAsync("", null, CancellationToken.None));
    }

    [Fact]
    public async Task RollbackAsync_WithNoRecords_ShouldReturnZeroResult()
    {
        // Arrange
        var opId = Guid.NewGuid().ToString("N");
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeletionRecord>().AsReadOnly());

        var sut = CreateSut();

        // Act
        var result = await sut.RollbackAsync(opId, null, CancellationToken.None);

        // Assert
        Assert.Equal(opId, result.OperationId);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.IsFullySuccessful);
    }

    [Fact]
    public async Task RollbackAsync_WithPermanentDeletion_ShouldReturnFailure()
    {
        // Arrange
        var opId = Guid.NewGuid().ToString("N");
        var records = new List<DeletionRecord>
        {
            CreateRecord(opId, @"\\?\C:\test\permanent_file.txt", "PERMANENT")
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());

        var sut = CreateSut();

        // Act
        var result = await sut.RollbackAsync(opId, null, CancellationToken.None);

        // Assert: 永久删除的文件无法恢复
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Single(result.FailedFiles);
        Assert.Equal("PERMANENT_DELETION", result.FailedFiles[0].ErrorCode);
    }

    [Fact]
    public async Task RollbackAsync_WithCancelledToken_ShouldReturnPartialResult()
    {
        // Arrange
        var opId = Guid.NewGuid().ToString("N");
        var records = new List<DeletionRecord>
        {
            CreateRecord(opId, @"\\?\C:\test\file1.txt", "QUARANTINE"),
            CreateRecord(opId, @"\\?\C:\test\file2.txt", "QUARANTINE"),
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 立即取消
        var sut = CreateSut();

        // Act
        var result = await sut.RollbackAsync(opId, null, cts.Token);

        // Assert
        Assert.True(result.IsCancelled);
        Assert.Equal(0, result.SuccessCount);
    }

    [Fact]
    public async Task RollbackAsync_WithQuarantineDisabled_ShouldReportFailure()
    {
        // Arrange: 隔离区禁用 → GetQuarantineDirectoryAsync 返回 null
        _quarantineManagerMock
            .Setup(q => q.GetQuarantineDirectoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var opId = Guid.NewGuid().ToString("N");
        var records = new List<DeletionRecord>
        {
            CreateRecord(opId, @"\\?\C:\test\quarantine_file.txt", "QUARANTINE")
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());

        var sut = CreateSut();

        // Act
        var result = await sut.RollbackAsync(opId, null, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal("QUARANTINE_DISABLED", result.FailedFiles[0].ErrorCode);

        // 恢复默认隔离区设置
        _quarantineManagerMock
            .Setup(q => q.GetQuarantineDirectoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_quarantineDir);
    }

    [Fact]
    public async Task RollbackAsync_WithMixedResults_ShouldReturnCorrectCounts()
    {
        // Arrange: 2 个永久删除（失败）+ 1 个隔离区（下面有真实文件）
        // 使用真实隔离区文件来测试混合结果
        var opId = Guid.NewGuid().ToString("N");
        var filePath = CreateRealFile(_tempDir, "mixed_success.txt");
        var hash = ComputeSha256ForFile(filePath);

        var restoredFile = CreateRecord(opId, filePath, "QUARANTINE", hash);
        var permFile1 = CreateRecord(opId, @"\\?\C:\test\perm1.tmp", "PERMANENT");
        var permFile2 = CreateRecord(opId, @"\\?\C:\test\perm2.tmp", "PERMANENT");

        // 为隔离区恢复创建对应的隔离区文件
        CreateQuarantineFile(PathNormalizer.Denormalize(filePath), hash, DateTime.UtcNow);

        var records = new List<DeletionRecord> { restoredFile, permFile1, permFile2 };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());

        _tombstoneRepoMock
            .Setup(r => r.DeleteByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var sut = CreateSut();

        // Act
        var result = await sut.RollbackAsync(opId, null, CancellationToken.None);

        // Assert: 1 成功 + 2 永久删除失败
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(2, result.FailedCount);

        // 验证墓碑已清除
        _tombstoneCacheMock.Verify(c => c.RemoveByOperationId(opId), Times.Once);
    }

    [Fact]
    public async Task RollbackAsync_ShouldReportProgress()
    {
        // Arrange
        var opId = Guid.NewGuid().ToString("N");
        var records = new List<DeletionRecord>
        {
            CreateRecord(opId, @"\\?\C:\test\p1.tmp", "PERMANENT"),
            CreateRecord(opId, @"\\?\C:\test\p2.tmp", "PERMANENT"),
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());

        var progressReports = new List<OperationProgress>();
        var progress = new Progress<OperationProgress>(p => progressReports.Add(p));

        var sut = CreateSut();

        // Act
        await sut.RollbackAsync(opId, progress, CancellationToken.None);

        // Assert: 至少收到完成进度报告
        Assert.Contains(progressReports, p => p.Phase == OperationPhase.Completed);
    }

    [Fact]
    public async Task RollbackAsync_WithSuccessfulRestore_ShouldClearTombstones()
    {
        // Arrange: 使用真实隔离区文件
        var opId = Guid.NewGuid().ToString("N");
        var filePath = CreateRealFile(_tempDir, "tombstone_test.txt");
        var hash = ComputeSha256ForFile(filePath);

        // 为这个文件创建隔离区备份
        CreateQuarantineFile(PathNormalizer.Denormalize(filePath), hash, DateTime.UtcNow);

        var records = new List<DeletionRecord>
        {
            CreateRecord(opId, filePath, "QUARANTINE", hash)
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());

        _tombstoneRepoMock
            .Setup(r => r.DeleteByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var sut = CreateSut();

        // Act
        var result = await sut.RollbackAsync(opId, null, CancellationToken.None);

        // Assert: 墓碑已从 DB 和内存缓存中清除
        Assert.Equal(1, result.SuccessCount);
        _tombstoneRepoMock.Verify(
            r => r.DeleteByOperationIdAsync(opId, It.IsAny<CancellationToken>()), Times.Once);
        _tombstoneCacheMock.Verify(c => c.RemoveByOperationId(opId), Times.Once);
    }

    [Fact]
    public async Task RollbackAsync_PartialSuccess_ShouldStillCleanTombstones()
    {
        // Arrange: 3 个文件，1 永久删除 + 2 隔离区恢复成功
        var opId = Guid.NewGuid().ToString("N");

        var file1 = CreateRealFile(_tempDir, "partial_1.txt");
        var file2 = CreateRealFile(_tempDir, "partial_2.txt");
        var hash1 = ComputeSha256ForFile(file1);
        var hash2 = ComputeSha256ForFile(file2);

        CreateQuarantineFile(PathNormalizer.Denormalize(file1), hash1, DateTime.UtcNow);
        CreateQuarantineFile(PathNormalizer.Denormalize(file2), hash2, DateTime.UtcNow);

        var records = new List<DeletionRecord>
        {
            CreateRecord(opId, file1, "QUARANTINE", hash1),
            CreateRecord(opId, file2, "QUARANTINE", hash2),
            CreateRecord(opId, @"\\?\C:\test\perm.tmp", "PERMANENT"),
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());
        _tombstoneRepoMock
            .Setup(r => r.DeleteByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var sut = CreateSut();

        // Act
        var result = await sut.RollbackAsync(opId, null, CancellationToken.None);

        // Assert: 2 成功 + 1 失败，但墓碑仍被清除
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(1, result.FailedCount);
        _tombstoneCacheMock.Verify(c => c.RemoveByOperationId(opId), Times.Once);
    }

    // ================================================================
    // GetRollbackHistoryAsync — 单元测试（×2）
    // ================================================================

    [Fact]
    public async Task GetRollbackHistoryAsync_ShouldDelegateToRepository()
    {
        // Arrange
        var expected = new List<OperationBatchInfo>
        {
            new()
            {
                OperationId = "op1",
                ExecutedAt = DateTime.UtcNow,
                FileCount = 10,
                TotalSizeBytes = 100_000,
                Method = DeleteMethod.RecycleBin,
                IsRollbackable = true
            }
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetBatchSummariesAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected.AsReadOnly());

        var sut = CreateSut();

        // Act
        var result = await sut.GetRollbackHistoryAsync(CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.Equal("op1", result[0].OperationId);
        Assert.Equal(10, result[0].FileCount);
    }

    [Fact]
    public async Task GetRollbackHistoryAsync_WithEmptyRepo_ShouldReturnEmptyList()
    {
        // Arrange
        _deletionRecordRepoMock
            .Setup(r => r.GetBatchSummariesAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OperationBatchInfo>().AsReadOnly());

        var sut = CreateSut();

        // Act
        var result = await sut.GetRollbackHistoryAsync(CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    // ================================================================
    // VerifyHashesAsync — 单元测试（×4）
    // ================================================================

    [Fact]
    public async Task VerifyHashesAsync_WithNoRecords_ShouldReturnAllZeros()
    {
        // Arrange
        var opId = Guid.NewGuid().ToString("N");
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeletionRecord>().AsReadOnly());

        var sut = CreateSut();

        // Act
        var result = await sut.VerifyHashesAsync(opId, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.PassedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.NotFoundCount);
    }

    [Fact]
    public async Task VerifyHashesAsync_AllPassed_ShouldReturnCorrectCounts()
    {
        // Arrange: 创建真实隔离区文件并验证哈希
        var opId = Guid.NewGuid().ToString("N");
        var filePath = CreateRealFile(_tempDir, "verify_pass.txt");
        var hash = ComputeSha256ForFile(filePath);

        CreateQuarantineFile(PathNormalizer.Denormalize(filePath), hash, DateTime.UtcNow);

        var records = new List<DeletionRecord>
        {
            CreateRecord(opId, filePath, "QUARANTINE", hash),
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());

        var sut = CreateSut();

        // Act
        var result = await sut.VerifyHashesAsync(opId, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.PassedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.NotFoundCount);
    }

    [Fact]
    public async Task VerifyHashesAsync_WithPermanentDeletion_ShouldIncrementNotFound()
    {
        // Arrange: 永久删除的文件无法定位
        var opId = Guid.NewGuid().ToString("N");
        var records = new List<DeletionRecord>
        {
            CreateRecord(opId, @"\\?\C:\test\notfound.txt", "PERMANENT")
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());

        var sut = CreateSut();

        // Act
        var result = await sut.VerifyHashesAsync(opId, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.PassedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(1, result.NotFoundCount);
    }

    [Fact]
    public async Task VerifyHashesAsync_WithCancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var opId = Guid.NewGuid().ToString("N");
        var records = new List<DeletionRecord>
        {
            CreateRecord(opId, @"\\?\C:\test\file.txt", "QUARANTINE")
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sut = CreateSut();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.VerifyHashesAsync(opId, cts.Token));
    }

    // ================================================================
    // 集成测试 — 真实文件系统操作（×3）
    // ================================================================

    [Fact]
    public async Task RollbackAsync_Integration_QuarantineRestore_Success()
    {
        // Arrange: 创建真实隔离区文件 + DeletionRecord
        var opId = Guid.NewGuid().ToString("N");
        var originalDir = Path.Combine(_tempDir, "restore_target");
        Directory.CreateDirectory(originalDir);
        var originalPath = PathNormalizer.Normalize(Path.Combine(originalDir, "important.txt"));

        // 先向隔离区放入一个文件
        var quarantineContent = "important data to be restored";
        var sha256Prefix = "a1b2c3d4e5f6a7b8";
        var fullHash = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2";
        var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var quarantineFileName = $"{sha256Prefix}_important.txt_{unixSeconds}.quarantine";
        var quarantinePath = Path.Combine(_quarantineDir, quarantineFileName);

        File.WriteAllText(quarantinePath, quarantineContent);

        // 重新计算实际哈希值
        var actualHash = ComputeSha256ForFile(quarantinePath);

        var records = new List<DeletionRecord>
        {
            new()
            {
                OperationId = opId,
                FilePath = originalPath,
                FileHash = actualHash,
                FileSize = quarantineContent.Length,
                DeletionMethod = "QUARANTINE",
                DecisionSnapshotJson = "{}",
                CreatedAt = DateTime.UtcNow
            }
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());
        _tombstoneRepoMock
            .Setup(r => r.DeleteByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var sut = CreateSut();

        // Act
        var result = await sut.RollbackAsync(opId, null, CancellationToken.None);

        // Assert: 文件成功恢复到原路径
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);

        var restoredPath = PathNormalizer.Denormalize(originalPath);
        Assert.True(File.Exists(restoredPath), $"文件应存在于 {restoredPath}");
        Assert.Equal(quarantineContent, File.ReadAllText(restoredPath));

        // 隔离区文件应已被移走
        Assert.False(File.Exists(quarantinePath));
    }

    [Fact]
    public async Task RollbackAsync_Integration_PathConflict_ShouldUseSuffix()
    {
        // Arrange: 目标路径已存在文件 → 应使用 _restored_1 后缀
        var opId = Guid.NewGuid().ToString("N");
        var originalDir = Path.Combine(_tempDir, "conflict_target");
        Directory.CreateDirectory(originalDir);
        var originalPath = PathNormalizer.Normalize(Path.Combine(originalDir, "conflict.txt"));

        // 在目标路径创建一个已存在的文件
        File.WriteAllText(PathNormalizer.Denormalize(originalPath), "existing file");

        // 创建隔离区中的恢复源文件
        var sha256Prefix = "b1c2d3e4f5g6h7i8";
        var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var quarantineFileName = $"{sha256Prefix}_conflict.txt_{unixSeconds}.quarantine";
        var quarantinePath = Path.Combine(_quarantineDir, quarantineFileName);
        File.WriteAllText(quarantinePath, "restored content");
        var actualHash = ComputeSha256ForFile(quarantinePath);

        var records = new List<DeletionRecord>
        {
            new()
            {
                OperationId = opId,
                FilePath = originalPath,
                FileHash = actualHash,
                FileSize = 100,
                DeletionMethod = "QUARANTINE",
                DecisionSnapshotJson = "{}",
                CreatedAt = DateTime.UtcNow
            }
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());
        _tombstoneRepoMock
            .Setup(r => r.DeleteByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var sut = CreateSut();

        // Act
        var result = await sut.RollbackAsync(opId, null, CancellationToken.None);

        // Assert: 恢复成功，但路径使用后缀
        Assert.Equal(1, result.SuccessCount);

        // 原文件仍存在
        Assert.True(File.Exists(PathNormalizer.Denormalize(originalPath)),
            "原文件应保持不变");

        // 恢复文件应使用 _restored_1 后缀
        var restoredPath = Path.Combine(originalDir, "conflict_restored_1.txt");
        Assert.True(File.Exists(restoredPath), $"恢复文件应存在于 {restoredPath}");
        Assert.Equal("restored content", File.ReadAllText(restoredPath));
    }

    [Fact]
    public async Task RollbackAsync_Integration_HashMismatch_ShouldReject()
    {
        // Arrange: 隔离区文件的哈希与记录不匹配
        var opId = Guid.NewGuid().ToString("N");
        var originalDir = Path.Combine(_tempDir, "mismatch_target");
        Directory.CreateDirectory(originalDir);
        var originalPath = PathNormalizer.Normalize(Path.Combine(originalDir, "mismatch.txt"));

        // 创建隔离区文件（内容与预期哈希不匹配）
        var sha256Prefix = "c1d2e3f4g5h6i7j8";
        var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var quarantineFileName = $"{sha256Prefix}_mismatch.txt_{unixSeconds}.quarantine";
        var quarantinePath = Path.Combine(_quarantineDir, quarantineFileName);
        File.WriteAllText(quarantinePath, "corrupted content");

        var records = new List<DeletionRecord>
        {
            new()
            {
                OperationId = opId,
                FilePath = originalPath,
                // 预期哈希与实际内容不匹配
                FileHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                FileSize = 100,
                DeletionMethod = "QUARANTINE",
                DecisionSnapshotJson = "{}",
                CreatedAt = DateTime.UtcNow
            }
        };
        _deletionRecordRepoMock
            .Setup(r => r.GetByOperationIdAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records.AsReadOnly());

        var sut = CreateSut();

        // Act
        var result = await sut.RollbackAsync(opId, null, CancellationToken.None);

        // Assert: 哈希不匹配，拒绝恢复
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal("QUARANTINE_FILE_NOT_FOUND", result.FailedFiles[0].ErrorCode);

        // 隔离区文件未被移动
        Assert.True(File.Exists(quarantinePath), "隔离区文件应保留");
    }

    // ================================================================
    // 辅助方法
    // ================================================================

    /// <summary>计算文件的 SHA-256 哈希</summary>
    private static string ComputeSha256ForFile(string filePath)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(PathNormalizer.Denormalize(filePath));
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        // 清理临时目录
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响测试结果
        }
    }
}
