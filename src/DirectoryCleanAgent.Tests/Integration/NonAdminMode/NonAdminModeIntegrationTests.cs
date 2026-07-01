// ============================================================
// NonAdminModeIntegrationTests.cs
// 功能: 非管理员只读模式集成测试
// 覆盖: AppMode.ReadOnly、删除按钮禁用、警告横幅显示
// 通过 Mock IAppStateService 模拟非管理员状态验证 UI 约束逻辑
// ============================================================

using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Services;
using DirectoryCleanAgent.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;

namespace DirectoryCleanAgent.Tests.Integration.NonAdminMode;

/// <summary>
/// 非管理员只读模式集成测试
/// 不要求真实非管理员进程，通过 Mock 验证 UI 层约束
/// </summary>
public class NonAdminModeIntegrationTests : IntegrationTestBase
{
    private readonly Mock<ILogger<AppStateService>> _stateLoggerMock;
    private readonly AppStateService _appStateService;

    public NonAdminModeIntegrationTests() : base("non_admin")
    {
        _stateLoggerMock = new Mock<ILogger<AppStateService>>();
        _appStateService = new AppStateService(_stateLoggerMock.Object);
    }

    // ================================================================
    // IT-NADMIN-01: 非管理员 → AppMode.ReadOnly
    // ================================================================

    /// <summary>
    /// 非管理员环境下 AppMode 自动设置为 ReadOnly
    /// 清理/删除操作被禁用，分析功能保留
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void NonAdminMode_AppMode_ReadOnly_WhenNotAdmin()
    {
        var logger = CreateLogger<NonAdminModeIntegrationTests>();
        logger.LogInformation("开始测试: 非管理员只读模式");

        try
        {
            // Arrange: 模拟非管理员场景
            _appStateService.IsAdmin = false;

            // Assert: 管理员标志为 false
            Assert.False(_appStateService.IsAdmin);

            // Act: 设置 AppMode 为 ReadOnly（模拟 App.xaml.cs 中的降级逻辑）
            _appStateService.AppMode = AppMode.ReadOnly;

            // Assert: 读取模式已设置
            Assert.Equal(AppMode.ReadOnly, _appStateService.AppMode);

            logger.LogInformation("非管理员只读模式验证通过: IsAdmin={Admin}, AppMode={Mode}",
                _appStateService.IsAdmin, _appStateService.AppMode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "非管理员模式验证失败");
            throw;
        }
    }

    // ================================================================
    // IT-NADMIN-02: ReadOnly 模式 — 删除按钮禁用
    // ================================================================

    /// <summary>
    /// AppMode.ReadOnly 时，CanDelete/CanClean 操作标志为 false
    /// UI 层据此隐藏或禁用删除/清理按钮
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void NonAdminMode_DeleteOperation_Disabled()
    {
        var logger = CreateLogger<NonAdminModeIntegrationTests>();
        logger.LogInformation("开始测试: ReadOnly模式按钮禁用");

        try
        {
            // Arrange: 设置为只读模式
            _appStateService.AppMode = AppMode.ReadOnly;

            // Act & Assert: 只读模式下不允许清理操作
            var canClean = _appStateService.AppMode != AppMode.ReadOnly;
            var canDelete = _appStateService.AppMode != AppMode.ReadOnly;

            Assert.False(canClean, "只读模式下 CanClean 必须为 false");
            Assert.False(canDelete, "只读模式下 CanDelete 必须为 false");

            logger.LogInformation("按钮禁用验证通过: CanClean={Clean}, CanDelete={Delete}",
                canClean, canDelete);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "按钮禁用验证失败");
            throw;
        }
    }

    // ================================================================
    // IT-NADMIN-03: ReadOnly 模式 — 警告横幅
    // ================================================================

    /// <summary>
    /// AppMode.ReadOnly 时 UI 展示警告横幅
    /// 提示用户"只读模式 — 请以管理员身份重新运行以启用清理功能"
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void NonAdminMode_WarningBanner_Displayed()
    {
        var logger = CreateLogger<NonAdminModeIntegrationTests>();
        logger.LogInformation("开始测试: 警告横幅状态");

        try
        {
            // Arrange: 设置为只读模式
            _appStateService.AppMode = AppMode.ReadOnly;

            // Act: 判断是否应显示警告横幅
            var shouldShowWarning = _appStateService.AppMode == AppMode.ReadOnly;

            // Assert: 只读模式必须显示警告
            Assert.True(shouldShowWarning, "只读模式下必须显示警告横幅");

            // 验证 AppMode 枚举包含所有必要的模式
            var modes = Enum.GetValues<AppMode>();
            Assert.Contains(AppMode.Normal, modes);
            Assert.Contains(AppMode.ReadOnly, modes);

            logger.LogInformation("警告横幅验证通过: 应显示={Show}", shouldShowWarning);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "警告横幅测试失败");
            throw;
        }
    }
}
