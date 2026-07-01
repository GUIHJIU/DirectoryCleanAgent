using System.Windows;
using Microsoft.Extensions.Logging;
using Moq;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Localization;
using DirectoryCleanAgent.Services;
using DirectoryCleanAgent.ViewModels;
using Xunit;

namespace DirectoryCleanAgent.Tests.UI;

/// <summary>
/// QuarantineViewModel 单元测试 — C9 阶段核心验证。
/// 验证非管理员只读模式下恢复/删除按钮禁用、警告横幅显示，
/// 以及管理员模式下所有功能正常可用。
/// </summary>
public class QuarantineViewModelTests
{
    private readonly Mock<ILogger<QuarantineViewModel>> _loggerMock;
    private readonly Mock<IQuarantineManager> _quarantineManagerMock;
    private readonly Mock<IBackupManager> _backupManagerMock;
    private readonly Mock<IConfigService> _configServiceMock;
    private readonly Mock<ILocalizationService> _localizationMock;
    private readonly Mock<IAppStateService> _appStateServiceMock;
    private readonly QuarantineViewModel _viewModel;

    public QuarantineViewModelTests()
    {
        _loggerMock = new Mock<ILogger<QuarantineViewModel>>();
        _quarantineManagerMock = new Mock<IQuarantineManager>();
        _backupManagerMock = new Mock<IBackupManager>();
        _configServiceMock = new Mock<IConfigService>();
        _localizationMock = new Mock<ILocalizationService>();
        _appStateServiceMock = new Mock<IAppStateService>();

        // 默认 Mock 行为：管理员模式
        _appStateServiceMock.Setup(s => s.IsAdmin).Returns(true);
        _appStateServiceMock.Setup(s => s.AppMode).Returns(AppMode.Normal);

        // Mock 隔离区统计返回默认值
        _quarantineManagerMock.Setup(q => q.GetQuarantineStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuarantineStats
            {
                TotalFileCount = 5,
                TotalSizeBytes = 10_000_000,
                QuarantineMaxSizeBytes = 100_000_000,
                ExpiredFileCount = 1
            });

        // Mock 隔离区文件列表返回空列表
        _quarantineManagerMock.Setup(q => q.ListQuarantineFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QuarantineFileEntry>());

        _viewModel = new QuarantineViewModel(
            _loggerMock.Object,
            _quarantineManagerMock.Object,
            _backupManagerMock.Object,
            _configServiceMock.Object,
            _localizationMock.Object,
            _appStateServiceMock.Object);
    }

    /// <summary>
    /// C9: 验证构造函数正确订阅 AppModeChanged 事件。
    /// </summary>
    [Fact]
    public void Constructor_SubscribesToAppModeChanged()
    {
        _appStateServiceMock.VerifyAdd(
            s => s.AppModeChanged += It.IsAny<EventHandler<AppMode>>(), Times.Once);
    }

    /// <summary>
    /// C9: 验证 IsAdmin/ShowReadOnlyWarning 反映 AppStateService 状态。
    /// </summary>
    [Fact]
    public void IsAdmin_ReflectsAppStateService()
    {
        // Arrange: Mock 已设置 IsAdmin=true（构造函数默认值）

        // Assert: 管理员模式
        Assert.True(_viewModel.IsAdmin);
        Assert.False(_viewModel.ShowReadOnlyWarning);
    }

    /// <summary>
    /// C9: 验证非管理员时显示只读警告横幅，操作按钮不可用。
    /// </summary>
    [Fact]
    public void NonAdmin_ShowsReadOnlyWarning_DisablesCommands()
    {
        // Arrange: 切换为非管理员状态
        _appStateServiceMock.Setup(s => s.IsAdmin).Returns(false);
        _appStateServiceMock.Setup(s => s.AppMode).Returns(AppMode.ReadOnly);

        // Act: 触发 AppMode 变更事件，模拟启动时从 App.xaml.cs 发出的通知
        // EventHandler<AppMode> 签名为 (object? sender, AppMode e)，Raise 需传两个参数
        _appStateServiceMock.Raise(s => s.AppModeChanged += null, null, AppMode.ReadOnly);

        // Assert: 只读模式属性正确
        Assert.False(_viewModel.IsAdmin);
        Assert.True(_viewModel.ShowReadOnlyWarning);
        Assert.NotNull(_viewModel.ReadOnlyWarningMessage);
        Assert.Contains("只读模式", _viewModel.ReadOnlyWarningMessage);

        // Assert: 操作按钮应禁用（无选中项时 CanRestore/CanDelete 为 false）
        Assert.False(_viewModel.CanRestore);
        Assert.False(_viewModel.CanDelete);
    }

    /// <summary>
    /// C9: 验证管理员模式下 CanRestore/CanDelete 在无选中项时为 false。
    /// </summary>
    [Fact]
    public void AdminRole_NoSelection_DisablesRestoreAndDelete()
    {
        // Arrange: IsAdmin=true 但 HasSelection=false（默认无文件）
        Assert.True(_viewModel.IsAdmin);

        // Assert: 无选中项时操作按钮不可用
        Assert.False(_viewModel.CanRestore);
        Assert.False(_viewModel.CanDelete);
    }

    /// <summary>
    /// C9: 验证切换到只读模式后自动刷新命令状态。
    /// </summary>
    [Fact]
    public void AppModeChanged_ToReadOnly_RefreshesCommandStates()
    {
        // Act: 最初是 Normal 模式
        Assert.True(_viewModel.IsAdmin);
        Assert.False(_viewModel.ShowReadOnlyWarning);

        // Act: 切换到 ReadOnly 模式
        _appStateServiceMock.Setup(s => s.IsAdmin).Returns(false);
        _appStateServiceMock.Setup(s => s.AppMode).Returns(AppMode.ReadOnly);
        _appStateServiceMock.Raise(s => s.AppModeChanged += null, null, AppMode.ReadOnly);

        // Assert: 状态已更新
        Assert.False(_viewModel.IsAdmin);
        Assert.True(_viewModel.ShowReadOnlyWarning);
    }

    /// <summary>
    /// C9: 验证切换到普通模式后恢复命令可用性。
    /// </summary>
    [Fact]
    public void AppModeChanged_ToNormal_RemovesReadOnlyWarning()
    {
        // Arrange: 先设为只读模式
        _appStateServiceMock.Setup(s => s.IsAdmin).Returns(false);
        _appStateServiceMock.Setup(s => s.AppMode).Returns(AppMode.ReadOnly);
        _appStateServiceMock.Raise(s => s.AppModeChanged += null, null, AppMode.ReadOnly);
        Assert.True(_viewModel.ShowReadOnlyWarning);

        // Act: 切换回 Normal 模式
        _appStateServiceMock.Setup(s => s.IsAdmin).Returns(true);
        _appStateServiceMock.Setup(s => s.AppMode).Returns(AppMode.Normal);
        _appStateServiceMock.Raise(s => s.AppModeChanged += null, null, AppMode.Normal);

        // Assert: 只读警告已清除
        Assert.True(_viewModel.IsAdmin);
        Assert.False(_viewModel.ShowReadOnlyWarning);
    }
}
