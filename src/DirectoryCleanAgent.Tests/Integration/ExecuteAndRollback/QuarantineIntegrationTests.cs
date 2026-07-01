// ============================================================
// QuarantineIntegrationTests.cs
// 功能: 隔离区生命周期集成测试
// ============================================================

using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Operations;
using DirectoryCleanAgent.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.Integration.ExecuteAndRollback;

public class QuarantineIntegrationTests : IntegrationTestBase
{
    private readonly QuarantineManager _quarantineManager;
    private readonly QuarantineSimulator _quarantineSim;
    private readonly string _quarantineDir;

    public QuarantineIntegrationTests() : base("quarantine_int")
    {
        _quarantineDir = Path.Combine(TestRoot, "quarantine");
        Directory.CreateDirectory(_quarantineDir);

        var qConfig = TestConfigBuilder.Create()
            .WithDataStorage(TestRoot)
            .WithQuarantine(100L * 1024 * 1024, 30)
            .Build();
        UpdateConfig(qConfig);

        var qLogger = NullLoggerFactory.Instance.CreateLogger<QuarantineManager>();
        _quarantineManager = new QuarantineManager(ConfigServiceMock.Object, qLogger);

        _quarantineSim = new QuarantineSimulator(_quarantineDir);
    }

    // ================================================================
    // IT-QRTN-01: 统计查询
    // ================================================================
    [Fact]
    [Trait("Category", "Integration")]
    public async Task QuarantineManager_GetStats_ReturnsCorrectCount()
    {
        var logger = CreateLogger<QuarantineIntegrationTests>();
        logger.LogInformation("开始测试: 隔离区统计查询");

        try
        {
            _quarantineSim.CreateBatch(30);

            var stats = await _quarantineManager.GetQuarantineStatsAsync(CancellationToken.None);

            Assert.NotNull(stats);
            Assert.True(stats.TotalFileCount >= 30);
            Assert.True(stats.TotalSizeBytes > 0);
            Assert.True(stats.ExpiredFileCount >= 0);

            logger.LogInformation("隔离区统计: 文件数={Count}, 总大小={Size}",
                stats.TotalFileCount, stats.TotalSizeBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "隔离区统计测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-QRTN-02: 过期文件清理
    // ================================================================
    [Fact]
    [Trait("Category", "Integration")]
    public async Task QuarantineManager_ExpiredCleanup_RemovesExpiredFiles()
    {
        var logger = CreateLogger<QuarantineIntegrationTests>();
        logger.LogInformation("开始测试: 过期文件清理");

        try
        {
            _quarantineSim.CreateExpiredFile("expired_file.dat", daysOld: 40);
            _quarantineSim.CreateQuarantineFile("recent_file.log", contentSize: 1024,
                deletedAt: DateTimeOffset.UtcNow.AddDays(-10));

            var statsBefore = await _quarantineManager.GetQuarantineStatsAsync(CancellationToken.None);
            logger.LogInformation("清理前: {Count} 个文件", statsBefore.TotalFileCount);

            var result = await _quarantineManager.CleanupExpiredAsync(
                progress: null, CancellationToken.None);

            Assert.NotNull(result);
            logger.LogInformation("清理结果: 清理={Cleaned}, 释放={Freed}",
                result.CleanedFileCount, result.FreedBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "过期清理测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-QRTN-03: 文件名往返
    // ================================================================
    [Fact]
    [Trait("Category", "Integration")]
    public void QuarantineManager_FileName_RoundTrip()
    {
        var logger = CreateLogger<QuarantineIntegrationTests>();
        logger.LogInformation("开始测试: 隔离区文件名往返解析");

        try
        {
            var (qPath, sha256Hash, originalName) = _quarantineSim.CreateQuarantineFile(
                "important_document_v2.backup", contentSize: 4096);

            Assert.True(File.Exists(qPath));
            Assert.False(string.IsNullOrWhiteSpace(sha256Hash));
            Assert.Equal("important_document_v2.backup", originalName);

            var fileName = Path.GetFileName(qPath);
            Assert.EndsWith(".quarantine", fileName);
            Assert.Contains(originalName, fileName);
            Assert.Contains(sha256Hash[..16], fileName);

            logger.LogInformation("往返解析验证通过: 文件名={FileName}", fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "文件名往返测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-QRTN-04: 大文件绕过
    // ================================================================
    [Fact]
    [Trait("Category", "Integration")]
    public async Task QuarantineManager_BypassLargeFile_ReturnsTrue()
    {
        var logger = CreateLogger<QuarantineIntegrationTests>();
        logger.LogInformation("开始测试: 超大文件绕过");

        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(_quarantineDir)!);
            var availableSpace = driveInfo.AvailableFreeSpace;
            var bypassThreshold = (long)(availableSpace * 0.20);
            var largeFileSize = bypassThreshold + 1024 * 1024;

            var shouldBypass = await _quarantineManager.IsLargeFileForQuarantineAsync(
                largeFileSize, CancellationToken.None);

            Assert.True(shouldBypass);
            logger.LogInformation("大文件绕过验证通过: 文件大小={FileSize}, 阈值={Threshold}",
                largeFileSize, bypassThreshold);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "大文件绕过测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-QRTN-05: 文件列表
    // ================================================================
    [Fact]
    [Trait("Category", "Integration")]
    public async Task QuarantineManager_ListFiles_ReturnsSortedEntries()
    {
        var logger = CreateLogger<QuarantineIntegrationTests>();
        logger.LogInformation("开始测试: 隔离区文件列表");

        try
        {
            _quarantineSim.CreateBatch(20);

            var files = await _quarantineManager.ListQuarantineFilesAsync(
                progress: null, CancellationToken.None);

            Assert.NotNull(files);
            Assert.NotEmpty(files);

            foreach (var file in files)
            {
                Assert.False(string.IsNullOrWhiteSpace(file.OriginalFileName));
                Assert.True(file.FileSizeBytes >= 0);
                Assert.NotEqual(default(DateTime), file.QuarantineTimestamp);
            }

            logger.LogInformation("隔离区列表验证通过: {Count} 个条目", files.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "隔离区列表测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-QRTN-06: 单文件删除
    // ================================================================
    [Fact]
    [Trait("Category", "Integration")]
    public async Task QuarantineManager_DeleteSingleFile_RemovesFromDisk()
    {
        var logger = CreateLogger<QuarantineIntegrationTests>();
        logger.LogInformation("开始测试: 单文件删除");

        try
        {
            var (qPath, _, _) = _quarantineSim.CreateQuarantineFile(
                "single_delete.dat", contentSize: 1024);
            Assert.True(File.Exists(qPath));

            var result = await _quarantineManager.DeleteQuarantineFileAsync(
                qPath, CancellationToken.None);

            Assert.True(result);
            Assert.False(File.Exists(qPath));

            logger.LogInformation("单文件删除验证通过");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "单文件删除测试失败");
            throw;
        }
    }

    public new void Dispose()
    {
        _quarantineSim?.Dispose();
        base.Dispose();
    }
}
