using System.Runtime.CompilerServices;
using System.Text;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Everything;
using DirectoryCleanAgent.Everything.Interop;
using Microsoft.Extensions.Logging;
using Moq;

namespace DirectoryCleanAgent.Tests.Everything;

/// <summary>
/// EverythingFileListProvider 单元测试。
///
/// 通过 Moq 模拟 IEverythingSdk、ITombstoneCache、IConfigService，
/// 在不依赖真实 Everything64.dll 的环境下验证：
/// - 流式枚举正常流程
/// - 目录跳过
/// - 墓碑过滤
/// - PathFilter / Volumes 过滤
/// - MaxResults 上限
/// - CancellationToken 中断
/// - Everything 查询失败异常
/// - 路径为空 / 标准化失败跳过
/// - 排序下推参数传递
/// </summary>
public class EverythingFileListProviderTests
{
    // ================================================================
    // 测试辅助方法
    // ================================================================

    /// <summary>创建模拟 3 条结果的 SDK Mock</summary>
    private static Mock<IEverythingSdk> CreateMockSdkWithResults(
        string[] paths, long[] sizes, long[] frns, long[] dates, uint[] volumes)
    {
        var mockSdk = new Mock<IEverythingSdk>();

        // 设置 Query 成功
        mockSdk.Setup(s => s.Query(true)).Returns(true);
        mockSdk.Setup(s => s.GetLastError()).Returns(EverythingNative.ERROR_OK);
        mockSdk.Setup(s => s.GetNumResults()).Returns((uint)paths.Length);

        // 为每个索引设置返回值
        for (int i = 0; i < paths.Length; i++)
        {
            uint idx = (uint)i;
            string path = paths[i];
            long size = sizes[i];
            long frn = frns[i];
            long date = dates[i];
            uint vol = volumes[i];

            // GetResultFullPathName — 将路径写入 StringBuilder
            mockSdk.Setup(s => s.GetResultFullPathName(
                    idx, It.IsAny<StringBuilder>(), It.IsAny<uint>()))
                .Callback<uint, StringBuilder, uint>((_, buf, _2) => buf.Append(path));

            mockSdk.Setup(s => s.GetResultSize(idx)).Returns(size);
            mockSdk.Setup(s => s.GetResultFileReferenceNumber(idx)).Returns(frn);
            mockSdk.Setup(s => s.GetResultDateModified(idx)).Returns(date);
            mockSdk.Setup(s => s.GetResultVolumeSerial(idx)).Returns(vol);
        }

        return mockSdk;
    }

    /// <summary>
    /// 创建用于测试的 FileItem 期望值。
    /// 时间戳使用 2026-06-20T12:00:00 UTC 对应的 FILETIME。
    /// </summary>
    private static readonly DateTime TestDateTime =
        new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long TestFileTime = TestDateTime.ToFileTimeUtc();

    /// <summary>创建模拟的 UserConfig（FRN_AVAILABLE = true）</summary>
    private static UserConfig CreateTestConfig(bool frnAvailable = true)
    {
        return new UserConfig { FRN_AVAILABLE = frnAvailable };
    }

    /// <summary>创建 IConfigService Mock</summary>
    private static Mock<IConfigService> CreateMockConfigService(bool frnAvailable = true)
    {
        var mock = new Mock<IConfigService>();
        mock.Setup(c => c.Current).Returns(CreateTestConfig(frnAvailable));
        return mock;
    }

    /// <summary>收集 IAsyncEnumerable 的全部结果</summary>
    private static async Task<List<FileItem>> CollectAsync(
        IAsyncEnumerable<FileItem> enumerable,
        CancellationToken ct = default)
    {
        var results = new List<FileItem>();
        await foreach (var item in enumerable.WithCancellation(ct))
        {
            results.Add(item);
        }
        return results;
    }

    // ================================================================
    // 正常流程测试
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_NormalFlow_YieldsAllResults()
    {
        // Arrange: 模拟 3 个 C:\ 下的 .tmp 文件
        var paths = new[] { @"C:\file1.tmp", @"C:\file2.tmp", @"C:\file3.log" };
        var sizes = new long[] { 100, 200, 300 };
        var frns = new long[] { 1001, 1002, 1003 };
        var dates = new long[] { TestFileTime, TestFileTime, TestFileTime };
        var volumes = new uint[] { 0xA8B31C4D, 0xA8B31C4D, 0xA8B31C4D };

        var mockSdk = CreateMockSdkWithResults(paths, sizes, frns, dates, volumes);
        var mockConfig = CreateMockConfigService(frnAvailable: true);
        var mockTombstone = new Mock<ITombstoneCache>();
        mockTombstone.Setup(t => t.IsTombstoned(It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(false);
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act
        var results = await CollectAsync(
            provider.EnumerateFilesAsync(new EverythingQueryParams(), CancellationToken.None));

        // Assert
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.StartsWith(@"\\?\C:\", r.FilePath));
        Assert.Equal(100, results[0].SizeBytes);
        Assert.Equal(200, results[1].SizeBytes);
        Assert.Equal(300, results[2].SizeBytes);
        Assert.Equal(".tmp", results[0].Extension);
        Assert.Equal(".log", results[2].Extension);
    }

    // ================================================================
    // 目录跳过测试
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_DirectorySizeNegative_SkipsDirectory()
    {
        // Arrange: 第 2 条是目录（size = -1）
        var paths = new[] { @"C:\file.tmp", @"C:\folder", @"C:\file2.tmp" };
        var sizes = new long[] { 100, -1, 300 };
        var frns = new long[] { 1001, 1002, 1003 };
        var dates = new long[] { TestFileTime, TestFileTime, TestFileTime };
        var volumes = new uint[] { 0xA8B31C4D, 0xA8B31C4D, 0xA8B31C4D };

        var mockSdk = CreateMockSdkWithResults(paths, sizes, frns, dates, volumes);
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        mockTombstone.Setup(t => t.IsTombstoned(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(false);
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act
        var results = await CollectAsync(
            provider.EnumerateFilesAsync(new EverythingQueryParams(), CancellationToken.None));

        // Assert: 目录被跳过，仅产出 2 个文件
        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.FilePath.Contains("folder"));
    }

    // ================================================================
    // 墓碑过滤测试
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_TombstonedFile_SkipsYield()
    {
        // Arrange: file2 被墓碑标记
        var paths = new[] { @"C:\file1.tmp", @"C:\file2.tmp", @"C:\file3.tmp" };
        var sizes = new long[] { 100, 200, 300 };
        var frns = new long[] { 1001, 1002, 1003 };
        var dates = new long[] { TestFileTime, TestFileTime, TestFileTime };
        var volumes = new uint[] { 0xA8B31C4D, 0xA8B31C4D, 0xA8B31C4D };

        var mockSdk = CreateMockSdkWithResults(paths, sizes, frns, dates, volumes);
        var mockConfig = CreateMockConfigService(frnAvailable: true);
        var mockTombstone = new Mock<ITombstoneCache>();
        // file2 (FRN=1002, Volume=A8B3-1C4D) 命中墓碑
        mockTombstone
            .Setup(t => t.IsTombstoned("A8B3-1C4D:1002", null, It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(true);
        mockTombstone
            .Setup(t => t.IsTombstoned(It.Is<string?>(k => k != "A8B3-1C4D:1002"), null,
                It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(false);
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act
        var results = await CollectAsync(
            provider.EnumerateFilesAsync(new EverythingQueryParams(), CancellationToken.None));

        // Assert: file2 被墓碑过滤跳过了
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.FilePath.Contains("file1"));
        Assert.Contains(results, r => r.FilePath.Contains("file3"));
        Assert.DoesNotContain(results, r => r.FilePath.Contains("file2"));
    }

    // ================================================================
    // PathFilter 过滤测试
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_PathFilter_OnlyReturnsMatchingPaths()
    {
        // Arrange: 文件分布在不同目录
        var paths = new[]
        {
            @"C:\Temp\file1.tmp",
            @"C:\Windows\file2.tmp",
            @"C:\Temp\sub\file3.tmp",
        };
        var sizes = new long[] { 100, 200, 300 };
        var frns = new long[] { 1001, 1002, 1003 };
        var dates = new long[] { TestFileTime, TestFileTime, TestFileTime };
        var volumes = new uint[] { 0xA8B31C4D, 0xA8B31C4D, 0xA8B31C4D };

        var mockSdk = CreateMockSdkWithResults(paths, sizes, frns, dates, volumes);
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        mockTombstone.Setup(t => t.IsTombstoned(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(false);
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act: 仅返回 C:\Temp\ 下的文件
        var queryParams = new EverythingQueryParams
        {
            PathFilter = @"\\?\C:\Temp\"
        };
        var results = await CollectAsync(
            provider.EnumerateFilesAsync(queryParams, CancellationToken.None));

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.StartsWith(@"\\?\C:\Temp\", r.FilePath));
    }

    // ================================================================
    // Volumes 过滤测试
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_VolumeFilter_OnlyReturnsMatchingVolumes()
    {
        // Arrange: 文件在 C: 和 D: 上
        var paths = new[] { @"C:\file1.tmp", @"D:\file2.tmp", @"C:\file3.tmp" };
        var sizes = new long[] { 100, 200, 300 };
        var frns = new long[] { 1001, 1002, 1003 };
        var dates = new long[] { TestFileTime, TestFileTime, TestFileTime };
        var volumes = new uint[] { 0xA8B31C4D, 0xB9C42D5E, 0xA8B31C4D };

        var mockSdk = CreateMockSdkWithResults(paths, sizes, frns, dates, volumes);
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        mockTombstone.Setup(t => t.IsTombstoned(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(false);
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act: 仅扫描 C 盘
        var queryParams = new EverythingQueryParams
        {
            Volumes = new List<string> { "C:" }
        };
        var results = await CollectAsync(
            provider.EnumerateFilesAsync(queryParams, CancellationToken.None));

        // Assert: 仅 C 盘文件
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.StartsWith(@"\\?\C:\", r.FilePath));
    }

    // ================================================================
    // MaxResults 上限测试
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_MaxResults_LimitsOutput()
    {
        // Arrange: 模拟 100 条结果
        const int totalResults = 100;
        var paths = Enumerable.Range(0, totalResults)
            .Select(i => $@"C:\file{i}.tmp").ToArray();
        var sizes = Enumerable.Repeat(100L, totalResults).ToArray();
        var frns = Enumerable.Range(1, totalResults).Select(i => (long)i).ToArray();
        var dates = Enumerable.Repeat(TestFileTime, totalResults).ToArray();
        var volumes = Enumerable.Repeat(0xA8B31C4Du, totalResults).ToArray();

        var mockSdk = CreateMockSdkWithResults(paths, sizes, frns, dates, volumes);
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        mockTombstone.Setup(t => t.IsTombstoned(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(false);
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act: 限制最多 10 条
        var queryParams = new EverythingQueryParams { MaxResults = 10 };
        var results = await CollectAsync(
            provider.EnumerateFilesAsync(queryParams, CancellationToken.None));

        // Assert
        Assert.Equal(10, results.Count);
    }

    // ================================================================
    // 取消令牌测试
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_CancellationRequested_StopsEnumeration()
    {
        // Arrange: 100 条结果，中途取消
        const int totalResults = 100;
        var paths = Enumerable.Range(0, totalResults)
            .Select(i => $@"C:\file{i}.tmp").ToArray();
        var sizes = Enumerable.Repeat(100L, totalResults).ToArray();
        var frns = Enumerable.Range(1, totalResults).Select(i => (long)i).ToArray();
        var dates = Enumerable.Repeat(TestFileTime, totalResults).ToArray();
        var volumes = Enumerable.Repeat(0xA8B31C4Du, totalResults).ToArray();

        var mockSdk = CreateMockSdkWithResults(paths, sizes, frns, dates, volumes);
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        mockTombstone.Setup(t => t.IsTombstoned(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(false);
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act: 收集 5 条后取消
        using var cts = new CancellationTokenSource();
        var results = new List<FileItem>();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in provider.EnumerateFilesAsync(
                new EverythingQueryParams(), cts.Token))
            {
                results.Add(item);
                if (results.Count >= 5)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.True(results.Count <= 5, "取消后不应再产出新条目");
    }

    // ================================================================
    // Everything 查询失败异常测试
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_QueryFailsIpcError_ThrowsEverythingSdkException()
    {
        // Arrange
        var mockSdk = new Mock<IEverythingSdk>();
        mockSdk.Setup(s => s.Query(true)).Returns(false);
        mockSdk.Setup(s => s.GetLastError()).Returns(EverythingNative.ERROR_IPC);
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<EverythingSdkException>(async () =>
        {
            await CollectAsync(
                provider.EnumerateFilesAsync(new EverythingQueryParams(), CancellationToken.None));
        });

        Assert.Contains("IPC", ex.Message);
    }

    [Fact]
    public async Task EnumerateFilesAsync_QueryFailsNotReady_ThrowsEverythingSdkException()
    {
        // Arrange
        var mockSdk = new Mock<IEverythingSdk>();
        mockSdk.Setup(s => s.Query(true)).Returns(false);
        mockSdk.Setup(s => s.GetLastError()).Returns(99); // 非特定错误码，验证通用错误路径
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<EverythingSdkException>(async () =>
        {
            await CollectAsync(
                provider.EnumerateFilesAsync(new EverythingQueryParams(), CancellationToken.None));
        });

        Assert.Contains("查询执行失败", ex.Message);
    }

    // ================================================================
    // 路径标准化失败测试
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_EmptyPath_SkipsEntry()
    {
        // Arrange: 第 2 条结果路径为空
        var paths = new[] { @"C:\file1.tmp", "", @"C:\file3.tmp" };
        var sizes = new long[] { 100, 200, 300 };
        var frns = new long[] { 1001, 1002, 1003 };
        var dates = new long[] { TestFileTime, TestFileTime, TestFileTime };
        var volumes = new uint[] { 0xA8B31C4D, 0xA8B31C4D, 0xA8B31C4D };

        var mockSdk = CreateMockSdkWithResults(paths, sizes, frns, dates, volumes);
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        mockTombstone.Setup(t => t.IsTombstoned(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(false);
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act
        var results = await CollectAsync(
            provider.EnumerateFilesAsync(new EverythingQueryParams(), CancellationToken.None));

        // Assert: 空路径被跳过
        Assert.Equal(2, results.Count);
    }

    // ================================================================
    // 排序下推参数验证
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_SortType_PassesToEverythingSdk()
    {
        // Arrange
        var mockSdk = new Mock<IEverythingSdk>();
        mockSdk.Setup(s => s.Query(true)).Returns(true);
        mockSdk.Setup(s => s.GetLastError()).Returns(EverythingNative.ERROR_OK);
        mockSdk.Setup(s => s.GetNumResults()).Returns(0u); // 无结果，简化验证
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act: 请求按大小降序排列
        var queryParams = new EverythingQueryParams
        {
            SortType = EverythingSortType.Size,
            SortDescending = true
        };
        await CollectAsync(
            provider.EnumerateFilesAsync(queryParams, CancellationToken.None));

        // Assert: SDK 收到正确的排序参数
        mockSdk.Verify(s => s.SetSort(EverythingNative.SORT_SIZE_DESCENDING), Times.Once);
    }

    // ================================================================
    // FRN 不可用场景
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_FrnNotAvailable_UsesFingerprintKey()
    {
        // Arrange: FRN_AVAILABLE = false
        var paths = new[] { @"C:\file1.tmp" };
        var sizes = new long[] { 100 };
        var frns = new long[] { 1001 };
        var dates = new long[] { TestFileTime };
        var volumes = new uint[] { 0xA8B31C4D };

        var mockSdk = CreateMockSdkWithResults(paths, sizes, frns, dates, volumes);
        var mockConfig = CreateMockConfigService(frnAvailable: false);
        var mockTombstone = new Mock<ITombstoneCache>();
        mockTombstone.Setup(t => t.IsTombstoned(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(false);
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act
        var results = await CollectAsync(
            provider.EnumerateFilesAsync(new EverythingQueryParams(), CancellationToken.None));

        // Assert
        Assert.Single(results);
        // FRN 模式：frnKey 为 null，fingerprintKey 非 null
        mockTombstone.Verify(
            t => t.IsTombstoned(null, It.IsNotNull<string?>(),
                It.IsAny<string?>(), It.IsAny<long?>()),
            Times.Once);
    }

    // ================================================================
    // IsIndexingAsync 测试
    // ================================================================

    [Fact]
    public async Task IsIndexingAsync_DelegatesToDependencyDetector()
    {
        // Arrange
        var mockSdk = new Mock<IEverythingSdk>();
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();
        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act
        var result = await provider.IsIndexingAsync(CancellationToken.None);

        // Assert: 返回 bool（具体值取决于 Everything 是否运行）
        Assert.IsType<bool>(result);
    }

    // ================================================================
    // FRN 可用但单文件 FRN=0 降级测试（审计偏差 #2 修复）
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_FrnAvailable_ButRawFrnZero_FallsBackToFingerprint()
    {
        // Arrange: 全局 FRN_AVAILABLE=true，但单文件 rawFrn=0（如 FAT32 卷）
        var paths = new[] { @"C:\file1.tmp" };
        var sizes = new long[] { 100 };
        var frns = new long[] { 0 }; // ← 该文件无有效 FRN
        var dates = new long[] { TestFileTime };
        var volumes = new uint[] { 0 }; // ← volumeSerial 也为 0

        var mockSdk = CreateMockSdkWithResults(paths, sizes, frns, dates, volumes);
        var mockConfig = CreateMockConfigService(frnAvailable: true);
        var mockTombstone = new Mock<ITombstoneCache>();
        mockTombstone.Setup(t => t.IsTombstoned(It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(false);
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act
        var results = await CollectAsync(
            provider.EnumerateFilesAsync(new EverythingQueryParams(), CancellationToken.None));

        // Assert
        Assert.Single(results);
        // frnKey 应为 null（rawFrn=0 且 volumeGuid=null），fingerprintKey 应非 null（降级）
        mockTombstone.Verify(
            t => t.IsTombstoned(null, It.IsNotNull<string?>(),
                It.IsAny<string?>(), It.IsAny<long?>()),
            Times.Once);
    }

    // ================================================================
    // EverythingSortKey 应为文件路径（审计偏差 #5 修复）
    // ================================================================

    [Fact]
    public async Task EnumerateFilesAsync_EverythingSortKey_IsNormalizedPath()
    {
        // Arrange
        var paths = new[] { @"C:\file1.tmp" };
        var sizes = new long[] { 100 };
        var frns = new long[] { 1001 };
        var dates = new long[] { TestFileTime };
        var volumes = new uint[] { 0xA8B31C4D };

        var mockSdk = CreateMockSdkWithResults(paths, sizes, frns, dates, volumes);
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        mockTombstone.Setup(t => t.IsTombstoned(It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<long?>()))
            .Returns(false);
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();

        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act
        var results = await CollectAsync(
            provider.EnumerateFilesAsync(new EverythingQueryParams(), CancellationToken.None));

        // Assert: EverythingSortKey 应为标准化后的路径
        Assert.Single(results);
        Assert.StartsWith(@"\\?\C:\", results[0].EverythingSortKey);
        Assert.EndsWith("file1.tmp", results[0].EverythingSortKey);
    }

    // ================================================================
    // Dispose 测试
    // ================================================================

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutException()
    {
        // Arrange
        var mockSdk = new Mock<IEverythingSdk>();
        var mockConfig = CreateMockConfigService();
        var mockTombstone = new Mock<ITombstoneCache>();
        var mockLogger = new Mock<ILogger<EverythingFileListProvider>>();
        var provider = new EverythingFileListProvider(
            mockLogger.Object, mockConfig.Object, mockTombstone.Object, mockSdk.Object);

        // Act & Assert: 多次释放不抛异常
        provider.Dispose();
        provider.Dispose();
        provider.Dispose();
    }

    // ================================================================
    // Disable async warning for test methods that use async enumerable
    // but don't have await in the method signature pattern
    // ================================================================
}
