using Microsoft.Extensions.Logging;
using Moq;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Everything;
using DirectoryCleanAgent.Everything.Installation;
using DirectoryCleanAgent.Services;
using DirectoryCleanAgent.ViewModels;
using Xunit;

namespace DirectoryCleanAgent.Tests.UI;

/// <summary>
/// SetupWizardViewModel 单元测试。
/// 验证向导步骤流转、模式切换、跳过逻辑和完成流程。
/// 使用 Moq 模拟 IEverythingDetector、IConfigService、IAppStateService。
/// </summary>
public class SetupWizardViewModelTests
{
    private readonly Mock<ILogger<SetupWizardViewModel>> _loggerMock;
    private readonly Mock<IConfigService> _configServiceMock;
    private readonly Mock<IEverythingDetector> _detectorMock;
    private readonly Mock<IAppStateService> _appStateServiceMock;
    private readonly Mock<IEverythingInstallService> _installServiceMock;
    private readonly UserConfig _userConfig;

    public SetupWizardViewModelTests()
    {
        _loggerMock = new Mock<ILogger<SetupWizardViewModel>>();
        _configServiceMock = new Mock<IConfigService>();
        _detectorMock = new Mock<IEverythingDetector>();
        _appStateServiceMock = new Mock<IAppStateService>();
        _installServiceMock = new Mock<IEverythingInstallService>();

        _userConfig = new UserConfig();
        _configServiceMock.Setup(c => c.Current).Returns(_userConfig);
        _configServiceMock.Setup(c => c.SaveAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // 默认检测成功，索引就绪
        _detectorMock.Setup(d => d.DetectAsync(It.IsAny<IConfigService>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EverythingDetectionResult.CreateSuccess(
                rawVersion: 0x010401E8, major: 1, minor: 4, rev: 1, build: 232,
                frnAvailable: true, isIndexing: false, elapsedMs: 250));

        _detectorMock.Setup(d => d.WaitForIndexAsync(
                It.IsAny<CancellationToken>(), It.IsAny<IProgress<int>?>()))
            .ReturnsAsync(true);
    }

    /// <summary>创建 ViewModel 实例的工厂方法</summary>
    private SetupWizardViewModel CreateViewModel()
    {
        return new SetupWizardViewModel(
            _loggerMock.Object,
            _configServiceMock.Object,
            _detectorMock.Object,
            _appStateServiceMock.Object,
            _installServiceMock.Object);
    }

    // ================================================================
    // 步骤初始化测试
    // ================================================================

    /// <summary>验证默认简单模式下步骤列表为 4 步（跳过 Step2 用户画像）</summary>
    [Fact]
    public void Constructor_SimpleMode_Creates4Steps()
    {
        var vm = CreateViewModel();

        Assert.Equal(4, vm.Steps.Count);
        Assert.Equal("环境检测", vm.Steps[0].Title);
        Assert.Equal("欢迎", vm.Steps[1].Title);
        // Step2 在简单模式下不存在
        Assert.Equal("保护与排除", vm.Steps[2].Title);
        Assert.Equal("AI 配置", vm.Steps[3].Title);
    }

    /// <summary>切换为专家模式后步骤列表应为 5 步（包含 Step2 用户画像）</summary>
    [Fact]
    public void SwitchToExpertMode_Creates5Steps()
    {
        var vm = CreateViewModel();
        vm.SelectedUserMode = UserMode.Expert;

        Assert.Equal(5, vm.Steps.Count);
        Assert.Equal("环境检测", vm.Steps[0].Title);
        Assert.Equal("欢迎", vm.Steps[1].Title);
        Assert.Equal("用户画像", vm.Steps[2].Title); // 专家模式特有
        Assert.Equal("保护与排除", vm.Steps[3].Title);
        Assert.Equal("AI 配置", vm.Steps[4].Title);
    }

    /// <summary>Step0 不可跳过，其他步骤均可跳过</summary>
    [Fact]
    public void Step0_IsNotSkippable_OthersAre()
    {
        var vm = CreateViewModel();

        Assert.False(vm.Steps[0].IsSkippable);
        Assert.True(vm.Steps[1].IsSkippable);
        Assert.True(vm.Steps[2].IsSkippable);
        Assert.True(vm.Steps[3].IsSkippable);
    }

    /// <summary>初始化时当前步骤为 Step0</summary>
    [Fact]
    public void Constructor_CurrentStepIndex_IsZero()
    {
        var vm = CreateViewModel();

        Assert.Equal(0, vm.CurrentStepIndex);
        Assert.True(vm.Steps[0].IsActive);
    }

    /// <summary>初始状态下 CanGoPrevious 为 false</summary>
    [Fact]
    public void Constructor_CanGoPrevious_IsFalse()
    {
        var vm = CreateViewModel();

        Assert.False(vm.CanGoPrevious);
    }

    // ================================================================
    // 步骤导航测试
    // ================================================================

    /// <summary>执行 Next 命令后步骤索引递增</summary>
    [Fact]
    public void NextCommand_IncrementsStepIndex()
    {
        var vm = CreateViewModel();
        // 模拟 Step0 完成，允许前进
        vm.IsDetectionComplete = true;

        vm.NextCommand.Execute(null);

        Assert.Equal(1, vm.CurrentStepIndex);
    }

    /// <summary>执行 Previous 命令后步骤索引递减</summary>
    [Fact]
    public void PreviousCommand_DecrementsStepIndex()
    {
        var vm = CreateViewModel();
        vm.IsDetectionComplete = true;
        vm.NextCommand.Execute(null); // → Step1

        vm.PreviousCommand.Execute(null); // → Step0

        Assert.Equal(0, vm.CurrentStepIndex);
    }

    /// <summary>Step0 完成前 CanGoNext 为 false（不可前进）</summary>
    [Fact]
    public void BeforeDetectionComplete_CannotGoNext()
    {
        var vm = CreateViewModel();

        Assert.False(vm.CanGoNext);
    }

    /// <summary>Step0 检测完成后 CanGoNext 为 true</summary>
    [Fact]
    public void AfterDetectionComplete_CanGoNext()
    {
        var vm = CreateViewModel();
        vm.IsDetectionComplete = true;

        Assert.True(vm.CanGoNext);
    }

    /// <summary>最后一步时 NextButtonText 显示"完成"</summary>
    [Fact]
    public void LastStep_NextButtonText_IsFinish()
    {
        var vm = CreateViewModel();
        vm.IsDetectionComplete = true;
        // 简单模式共 4 步，最后一步索引为 3
        vm.CurrentStepIndex = 3;

        Assert.True(vm.IsLastStep);
        Assert.Equal("完成", vm.NextButtonText);
    }

    // ================================================================
    // 模式切换测试
    // ================================================================

    /// <summary>切换到专家模式后 IsExpertMode 为 true</summary>
    [Fact]
    public void SelectExpertMode_SetsIsExpertTrue()
    {
        var vm = CreateViewModel();
        vm.SelectedUserMode = UserMode.Expert;

        Assert.True(vm.IsExpertMode);
        Assert.False(vm.IsSimpleMode);
    }

    /// <summary>从专家模式切换回简单模式后步骤列表恢复 4 步</summary>
    [Fact]
    public void SwitchBackToSimpleMode_Restores4Steps()
    {
        var vm = CreateViewModel();
        vm.SelectedUserMode = UserMode.Expert;
        Assert.Equal(5, vm.Steps.Count);

        vm.SelectedUserMode = UserMode.Simple;
        Assert.Equal(4, vm.Steps.Count);
    }

    // ================================================================
    // 跳过向导测试
    // ================================================================

    /// <summary>跳过向导后 IsWizardSkipped 为 true，触发 WizardSkipped 事件</summary>
    [Fact]
    public async Task SkipWizard_InvokesWizardSkippedEvent()
    {
        var vm = CreateViewModel();
        var eventFired = false;
        vm.WizardSkipped += (s, e) => eventFired = true;

        vm.SkipWizardCommand.Execute(null);
        // SkipWizard 内部是 async void，需要等待
        await Task.Delay(200);

        Assert.True(vm.IsWizardSkipped);
        Assert.True(eventFired);
    }

    /// <summary>跳过向导后 IsFirstRun 设为 false</summary>
    [Fact]
    public async Task SkipWizard_SetsIsFirstRunFalse()
    {
        var vm = CreateViewModel();

        vm.SkipWizardCommand.Execute(null);
        await Task.Delay(200);

        Assert.False(vm.UserConfig.IsFirstRun);
    }

    // ================================================================
    // 向导完成测试
    // ================================================================

    /// <summary>最后一步执行 Next 后触发 WizardCompleted 事件</summary>
    [Fact]
    public async Task FinishWizard_InvokesWizardCompletedEvent()
    {
        var vm = CreateViewModel();
        vm.IsDetectionComplete = true;
        vm.CurrentStepIndex = 3; // 简单模式最后一步
        var eventFired = false;
        vm.WizardCompleted += (s, e) => eventFired = true;

        vm.NextCommand.Execute(null);
        await Task.Delay(200);

        Assert.True(eventFired);
    }

    /// <summary>向导完成后 IsFirstRun 设为 false</summary>
    [Fact]
    public async Task FinishWizard_SetsIsFirstRunFalse()
    {
        var vm = CreateViewModel();
        vm.IsDetectionComplete = true;
        vm.CurrentStepIndex = 3;

        vm.NextCommand.Execute(null);
        await Task.Delay(200);

        Assert.False(vm.UserConfig.IsFirstRun);
    }

    // ================================================================
    // Everything 检测测试
    // ================================================================

    /// <summary>Everything 检测完成后 IsDetectionComplete 为 true</summary>
    [Fact]
    public async Task RunEverythingDetection_SetsIsDetectionComplete()
    {
        var vm = CreateViewModel();
        Assert.False(vm.IsDetectionComplete);

        await vm.RunEverythingDetectionAsync();

        Assert.True(vm.IsDetectionComplete);
    }

    /// <summary>Everything 检测失败时设置 DetectionError</summary>
    [Fact]
    public async Task RunEverythingDetection_Failure_SetsError()
    {
        _detectorMock.Setup(d => d.DetectAsync(It.IsAny<IConfigService>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EverythingDetectionResult.CreateFailure("测试失败", 100));
        var vm = CreateViewModel();

        await vm.RunEverythingDetectionAsync();

        Assert.NotNull(vm.DetectionError);
        Assert.Contains("测试失败", vm.DetectionError);
    }

    /// <summary>检测完成后 IsDetecting 为 false</summary>
    [Fact]
    public async Task RunEverythingDetection_ResetsIsDetectingOnComplete()
    {
        var vm = CreateViewModel();

        await vm.RunEverythingDetectionAsync();

        Assert.False(vm.IsDetecting);
    }

    // ================================================================
    // 索引等待测试
    // ================================================================

    /// <summary>索引等待命令触发 IsWaitingForIndex 状态</summary>
    [Fact]
    public async Task StartIndexWait_SetsIsWaitingForIndex()
    {
        // 使用 TaskCompletionSource 控制 WaitForIndexAsync 的完成时机
        var tcs = new TaskCompletionSource<bool>();
        _detectorMock.Setup(d => d.WaitForIndexAsync(
                It.IsAny<CancellationToken>(), It.IsAny<IProgress<int>?>()))
            .Returns(tcs.Task);
        var vm = CreateViewModel();

        vm.StartIndexWaitCommand.Execute(1);

        Assert.True(vm.IsWaitingForIndex);

        // 完成等待以清理
        tcs.SetResult(true);
        await Task.Delay(50);
    }

    /// <summary>后台等待命令触发 IsBackgroundWaiting 状态</summary>
    [Fact]
    public async Task StartBackgroundWait_SetsIsBackgroundWaiting()
    {
        // 使用 TaskCompletionSource 防止 WaitForIndexAsync 立即完成
        var tcs = new TaskCompletionSource<bool>();
        _detectorMock.Setup(d => d.WaitForIndexAsync(
                It.IsAny<CancellationToken>(), It.IsAny<IProgress<int>?>()))
            .Returns(tcs.Task);
        var vm = CreateViewModel();

        vm.StartBackgroundWaitCommand.Execute(null);

        // IsBackgroundWaiting 是同步设置的
        Assert.True(vm.IsBackgroundWaiting);

        tcs.SetResult(true);
        await Task.Delay(100);
    }

    /// <summary>取消索引等待调用 CancelIndexWaitCommand 不抛异常</summary>
    [Fact]
    public async Task CancelIndexWait_DoesNotThrow()
    {
        var tcs = new TaskCompletionSource<bool>();
        _detectorMock.Setup(d => d.WaitForIndexAsync(
                It.IsAny<CancellationToken>(), It.IsAny<IProgress<int>?>()))
            .Returns(tcs.Task);
        var vm = CreateViewModel();
        vm.StartIndexWaitCommand.Execute(1);

        // 取消不应抛异常
        var ex = Record.Exception(() => vm.CancelIndexWaitCommand.Execute(null));
        Assert.Null(ex);

        tcs.TrySetCanceled();
        await Task.Delay(50);
    }

    // ================================================================
    // 跳过当前步骤测试
    // ================================================================

    /// <summary>Step1 可以通过 SkipStepCommand 跳过</summary>
    [Fact]
    public void SkipStep_SkipsCurrentStep()
    {
        var vm = CreateViewModel();
        vm.IsDetectionComplete = true;
        vm.CurrentStepIndex = 1; // 直接在 Step1

        Assert.True(vm.CanSkipCurrentStep);
        vm.SkipStepCommand.Execute(null);

        Assert.Equal(2, vm.CurrentStepIndex);
    }

    // ================================================================
    // RequestMinimizeToTray 事件测试
    // ================================================================

    /// <summary>后台等待命令触发 RequestMinimizeToTray 事件</summary>
    [Fact]
    public void StartBackgroundWait_FiresRequestMinimizeToTray()
    {
        var vm = CreateViewModel();
        var eventFired = false;
        vm.RequestMinimizeToTray += (s, e) => eventFired = true;

        vm.StartBackgroundWaitCommand.Execute(null);

        Assert.True(eventFired);
    }

    // ================================================================
    // 步骤构建边界测试
    // ================================================================

    /// <summary>简单模式下没有"用户画像"步骤</summary>
    [Fact]
    public void SimpleMode_NoProfileStep()
    {
        var vm = CreateViewModel();
        vm.SelectedUserMode = UserMode.Simple;

        Assert.DoesNotContain(vm.Steps, s => s.Title == "用户画像");
    }

    /// <summary>专家模式下包含"用户画像"步骤</summary>
    [Fact]
    public void ExpertMode_HasProfileStep()
    {
        var vm = CreateViewModel();
        vm.SelectedUserMode = UserMode.Expert;

        Assert.Contains(vm.Steps, s => s.Title == "用户画像");
    }

    /// <summary>所有步骤的 Index 应连续递增</summary>
    [Fact]
    public void Steps_HaveSequentialIndices()
    {
        var vm = CreateViewModel();

        for (int i = 0; i < vm.Steps.Count; i++)
        {
            Assert.Equal(i, vm.Steps[i].Index);
        }
    }
}
