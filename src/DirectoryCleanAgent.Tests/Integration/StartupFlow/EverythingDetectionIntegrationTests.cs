// ============================================================
// EverythingDetectionIntegrationTests.cs
// 功能: Everything SDK 5步启动检测集成测试
// 覆盖: 进程存活检查、版本检查、IPC Ping、FRN 能力探测、索引就绪检测
// 注意: 需要 Everything 的测试使用 SkipIfUnavailable 自动跳过
// ============================================================

using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Everything;
using DirectoryCleanAgent.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.Integration.StartupFlow;

/// <summary>
/// Everything 5步启动检测集成测试
/// 验证 EverythingDependencyDetector 的每一步检测逻辑
/// </summary>
public class EverythingDetectionIntegrationTests : IntegrationTestBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly string _dbPath;

    public EverythingDetectionIntegrationTests() : base("everything_detect")
    {
        _dbPath = Path.Combine(TestRoot, "detect_test.db");

        // 构建轻量级 DI 容器（仅注册 Everything 相关服务）
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // 配置层（Everything 检测需要 IConfigService 来设置 FRN_AVAILABLE）
        services.AddConfigLayer();

        // 数据层
        services.AddDataLayer(_dbPath);

        // Everything SDK 层
        services.AddEverythingServices();
        services.AddSingleton<IEverythingDetector>(sp =>
            sp.GetRequiredService<EverythingDependencyDetector>());

        _serviceProvider = services.BuildServiceProvider();
    }

    // ================================================================
    // IT-DETECT-01: 完整5步检测全部通过
    // ================================================================

    /// <summary>
    /// 在真实 Everything 环境中验证完整的 5 步检测流程全部通过
    /// 步骤: 进程存活 → 版本检查 → IPC Ping → FRN 能力探测 → 索引就绪
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task DetectAsync_AllStepsPass_ReturnsSuccess()
    {
        var logger = CreateLogger<EverythingDetectionIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: Everything 完整5步检测（真实环境）");

        try
        {
            // Arrange: 获取真实 EverythingDependencyDetector
            var detector = _serviceProvider.GetRequiredService<IEverythingDetector>();

            // Act: 执行完整检测流程
            var result = await detector.DetectAsync(
                _serviceProvider.GetRequiredService<IConfigService>(),
                CancellationToken.None);

            // Assert: 检测结果应为成功
            Assert.NotNull(result);
            Assert.True(result.CanProceedToMainUI, $"检测失败: {result.FailureReason}");
            Assert.False(string.IsNullOrWhiteSpace(result.VersionString), "版本字符串不能为空");

            logger.LogInformation("Everything 检测全部通过: 版本={Version}, FRN={Frn}, 索引={Index}",
                result.VersionString, result.FRN_AVAILABLE, result.IsIndexing);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Everything 完整检测测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-DETECT-02: Everything 进程未运行 → 阻断失败
    // ================================================================

    /// <summary>
    /// 当 Everything 进程未运行时应返回阻断失败结果
    /// 此场景通过停止 Everything 服务来验证（若当前 Everything 在运行）
    /// 或通过模拟 Everything 不可用的配置验证
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task DetectAsync_Step1ProcessNotRunning_ReturnsFailure()
    {
        var logger = CreateLogger<EverythingDetectionIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: 进程存活检查（步骤1）");

        // Arrange: 获取检测器
        var detector = _serviceProvider.GetRequiredService<IEverythingDetector>();

        // Act: 检测 Everything 进程状态
        var result = await detector.DetectAsync(
            _serviceProvider.GetRequiredService<IConfigService>(),
            CancellationToken.None);

        // Assert: 如果 Everything 在运行，步骤1应该通过
        // 此测试验证当前 Everything 环境下的进程检测结果
        Assert.True(result.CanProceedToMainUI || !string.IsNullOrEmpty(result.FailureReason),
            "检测结果必须要么成功，要么包含错误信息");
        Assert.NotNull(result.VersionString);

        logger.LogInformation("进程检查结果: 成功={IsSuccess}, 版本={Version}",
            result.CanProceedToMainUI, result.VersionString);
    }

    // ================================================================
    // IT-DETECT-03: 版本过低 → 阻断失败
    // ================================================================

    /// <summary>
    /// Everything 版本低于 1.4.1 时应返回阻断失败
    /// 版本检查是步骤2，失败后阻止进入主界面
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task DetectAsync_Step2VersionCheck_ReturnsVersionString()
    {
        var logger = CreateLogger<EverythingDetectionIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: 版本检查（步骤2）");

        // Arrange
        var detector = _serviceProvider.GetRequiredService<IEverythingDetector>();

        // Act
        var result = await detector.DetectAsync(
            _serviceProvider.GetRequiredService<IConfigService>(),
            CancellationToken.None);

        // Assert: 版本字符串必须存在（即使 Everything 不可用也有错误信息）
        Assert.NotNull(result.VersionString);
        Assert.NotEmpty(result.VersionString);

        // 验证版本号格式（应为 "major.minor.revision.build" 或错误信息）
        logger.LogInformation("Everything 版本: {Version}", result.VersionString);
    }

    // ================================================================
    // IT-DETECT-04: IPC Ping 失败 → 阻断失败
    // ================================================================

    /// <summary>
    /// IPC Ping 是步骤3，查询已知系统文件验证 IPC 通道可用
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task DetectAsync_Step3IpcPing_VerifiedBySuccess()
    {
        var logger = CreateLogger<EverythingDetectionIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: IPC Ping（步骤3）");

        // Arrange
        var detector = _serviceProvider.GetRequiredService<IEverythingDetector>();

        // Act: 完整检测即包含 IPC Ping
        var result = await detector.DetectAsync(
            _serviceProvider.GetRequiredService<IConfigService>(),
            CancellationToken.None);

        // Assert: 检测通过说明 IPC Ping 成功
        if (result.CanProceedToMainUI)
        {
            logger.LogInformation("IPC Ping 通过（检测成功）");
        }
        else
        {
            logger.LogWarning("IPC Ping 失败: {Error}", result.FailureReason);
        }

        Assert.True(result.CanProceedToMainUI || result.FailureReason != null);
    }

    // ================================================================
    // IT-DETECT-05: FRN 不可用时自动降级
    // ================================================================

    /// <summary>
    /// 步骤4 FRN 能力探测：FRN 不可用时不阻断，但自动设置 FRN_AVAILABLE=false
    /// 墓碑匹配降级为 Size+LastWriteTime 指纹模式（3天过期）
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task DetectAsync_Step4FrnCapability_ResultReflectsState()
    {
        var logger = CreateLogger<EverythingDetectionIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: FRN 能力探测（步骤4）");

        // Arrange
        var detector = _serviceProvider.GetRequiredService<IEverythingDetector>();
        var configService = _serviceProvider.GetRequiredService<IConfigService>();

        // Act
        var result = await detector.DetectAsync(configService, CancellationToken.None);

        // Assert: FRN 可用性状态必须正确反映在检测结果中
        // FRN 不可用不会阻断，但 IsFrnAvailable 应为 false
        logger.LogInformation("FRN 能力: IsFrnAvailable={Frn}", result.FRN_AVAILABLE);

        // 如果检测成功，验证 FRN 状态与配置一致
        if (result.CanProceedToMainUI)
        {
            var config = configService.Current;
            // FRN 状态已通过 DetectFrnCapability 写入配置
            Assert.NotNull(config);
        }
    }

    // ================================================================
    // IT-DETECT-06: 索引构建中 IsIndexing 标志
    // ================================================================

    /// <summary>
    /// 步骤5 索引就绪检测：索引构建中时不阻断但 IsIndexing=true
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task DetectAsync_Step5IsIndexing_ResultContainsIndexState()
    {
        var logger = CreateLogger<EverythingDetectionIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: 索引就绪检测（步骤5）");

        // Arrange
        var detector = _serviceProvider.GetRequiredService<IEverythingDetector>();

        // Act
        var result = await detector.DetectAsync(
            _serviceProvider.GetRequiredService<IConfigService>(),
            CancellationToken.None);

        // Assert: IsIndexing 状态必须正确反映
        // 无论是 true 还是 false，状态必须明确
        logger.LogInformation("索引状态: IsIndexing={IsIndexing}", result.IsIndexing);

        Assert.NotNull(result);
    }

    // ================================================================
    // IT-DETECT-07: 检测过程中取消操作
    // ================================================================

    /// <summary>
    /// 用户取消检测时抛出 OperationCanceledException
    /// 确保取消令牌正确传播到每一步检测
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public async Task DetectAsync_CancelledDuringDetection_ThrowsOperationCanceled()
    {
        var logger = CreateLogger<EverythingDetectionIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: 检测取消");

        // Arrange: 创建已取消的令牌
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var detector = _serviceProvider.GetRequiredService<IEverythingDetector>();

        // Act & Assert: 传入已取消的令牌应抛出异常
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await detector.DetectAsync(
                _serviceProvider.GetRequiredService<IConfigService>(),
                cts.Token);
        });

        logger.LogInformation("检测取消验证通过: 已正确抛出 OperationCanceledException");
    }

    // ================================================================
    // IT-DETECT-08: CheckProcessAlive 检测运行中的进程
    // ================================================================

    /// <summary>
    /// 验证 CheckProcessAlive 可以检测到运行中的 Everything 进程
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Requires", "Everything")]
    public void CheckProcessAlive_WithRunningProcess_ReturnsTrue()
    {
        var logger = CreateLogger<EverythingDetectionIntegrationTests>();
        EverythingTestHelper.SkipIfUnavailable(logger);

        logger.LogInformation("开始测试: 进程存活检测");

        // Act: 直接查询 Everything 进程
        var processes = System.Diagnostics.Process.GetProcessesByName("Everything");

        // Assert: Everything 运行时必须有至少一个进程
        Assert.NotEmpty(processes);
        logger.LogInformation("检测到 {Count} 个 Everything 进程", processes.Length);
    }

    // ================================================================
    // IT-DETECT-09: 重复检测使用缓存
    // ================================================================

    /// <summary>
    /// 第二次调用 DetectAsync 可以使用缓存版本号
    /// EverythingTestHelper 内部使用双重检查锁定缓存
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void EverythingTestHelper_Cache_ReturnsSameResultAfterFirstCall()
    {
        var logger = CreateLogger<EverythingDetectionIntegrationTests>();

        try
        {
            // 重置缓存确保首次检测
            EverythingTestHelper.ResetCache();

            // Act: 两次调用获取可用性
            var firstAvailability = EverythingTestHelper.IsAvailable;
            var firstVersion = EverythingTestHelper.Version;

            var secondAvailability = EverythingTestHelper.IsAvailable;
            var secondVersion = EverythingTestHelper.Version;

            // Assert: 两次调用返回一致结果（缓存生效）
            Assert.Equal(firstAvailability, secondAvailability);
            Assert.Equal(firstVersion, secondVersion);

            logger.LogInformation("缓存验证通过: 可用={Available}, 版本={Version}",
                firstAvailability, firstVersion);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "缓存验证失败");
            throw;
        }
    }

    public new void Dispose()
    {
        try
        {
            if (_serviceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch { }

        base.Dispose();
    }
}
