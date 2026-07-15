using DirectoryCleanAgent.AI;
using DirectoryCleanAgent.AI.Models;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using Microsoft.Extensions.Logging;
using Moq;

namespace DirectoryCleanAgent.Tests.AI;

public class AiAnalysisCoordinatorTests
{
    [Fact]
    public async Task AnalyzeBatchAsync_SameFileTwice_OnlyCallsAdvisorOnce()
    {
        // Arrange
        var advisorMock = new Mock<IAiAdvisorService>();
        var configMock = new Mock<IConfigService>();
        configMock.Setup(c => c.Current).Returns(new UserConfig
        {
            AIEnabled = true,
            AIAutoAnalyze = true,
            UserMode = UserMode.Expert
        });
        advisorMock.Setup(a => a.IsAvailable).Returns(true);
        advisorMock.Setup(a => a.AnalyzeFilesAsync(It.IsAny<IReadOnlyList<FileDecisionCache>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AiAnalysisResult>
            {
                AiAnalysisResult.Success(@"C:\test\file1.tmp", "safe", 0.9, "test")
            });

        var logger = new Mock<ILogger<AiAnalysisCoordinator>>().Object;
        var coordinator = new AiAnalysisCoordinator(advisorMock.Object, configMock.Object, logger);

        var cache = new FileDecisionCache
        {
            FilePath = @"C:\test\file1.tmp",
            SizeBytes = 200L * 1024 * 1024,
            LastWriteTime = DateTime.UtcNow,
            RuleVerdict = RuleVerdict.SuggestDelete,
            SemanticCategory = "Uncategorized",
            FinalAction = FinalAction.SuggestDelete,
            CacheVersion = 1
        };

        // Act
        await coordinator.AnalyzeBatchAsync(new[] { cache }, CancellationToken.None);
        await coordinator.AnalyzeBatchAsync(new[] { cache }, CancellationToken.None);

        // Assert — advisor 只被调用一次
        advisorMock.Verify(a => a.AnalyzeFilesAsync(
            It.IsAny<IReadOnlyList<FileDecisionCache>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TriggerAutoAnalyzeAsync_AiDisabled_ReturnsEmpty()
    {
        // Arrange
        var advisorMock = new Mock<IAiAdvisorService>();
        var configMock = new Mock<IConfigService>();
        configMock.Setup(c => c.Current).Returns(new UserConfig
        {
            AIEnabled = false,  // AI 禁用
            AIAutoAnalyze = true,
            UserMode = UserMode.Expert
        });
        var logger = new Mock<ILogger<AiAnalysisCoordinator>>().Object;
        var coordinator = new AiAnalysisCoordinator(advisorMock.Object, configMock.Object, logger);

        // Act
        var results = await coordinator.TriggerAutoAnalyzeAsync(
            Array.Empty<FileDecisionCache>(), CancellationToken.None);

        // Assert
        Assert.Empty(results);
        advisorMock.Verify(a => a.AutoAnalyzeUncategorizedAsync(
            It.IsAny<IReadOnlyList<FileDecisionCache>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TriggerAutoAnalyzeAsync_SimpleMode_ReturnsEmpty()
    {
        // Arrange
        var advisorMock = new Mock<IAiAdvisorService>();
        var configMock = new Mock<IConfigService>();
        configMock.Setup(c => c.Current).Returns(new UserConfig
        {
            AIEnabled = true,
            AIAutoAnalyze = true,
            UserMode = UserMode.Simple  // 简单模式
        });
        var logger = new Mock<ILogger<AiAnalysisCoordinator>>().Object;
        var coordinator = new AiAnalysisCoordinator(advisorMock.Object, configMock.Object, logger);

        // Act
        var results = await coordinator.TriggerAutoAnalyzeAsync(
            Array.Empty<FileDecisionCache>(), CancellationToken.None);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void CancelCurrentAnalysis_Requested_CancellationTokenIsCancelled()
    {
        // Arrange
        var advisorMock = new Mock<IAiAdvisorService>();
        var configMock = new Mock<IConfigService>();
        configMock.Setup(c => c.Current).Returns(new UserConfig());
        advisorMock.Setup(a => a.IsAvailable).Returns(true);
        var logger = new Mock<ILogger<AiAnalysisCoordinator>>().Object;
        var coordinator = new AiAnalysisCoordinator(advisorMock.Object, configMock.Object, logger);

        // 启动一个批量分析（在后台运行）
        var cache = new FileDecisionCache
        {
            FilePath = @"C:\test\file1.tmp",
            SizeBytes = 200L * 1024 * 1024,
            LastWriteTime = DateTime.UtcNow,
            RuleVerdict = RuleVerdict.SuggestDelete,
            SemanticCategory = "Uncategorized",
            FinalAction = FinalAction.SuggestDelete,
            CacheVersion = 1
        };
        // 设置 advisor mock 延迟返回以模拟运行中
        advisorMock.Setup(a => a.AnalyzeFilesAsync(It.IsAny<IReadOnlyList<FileDecisionCache>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<FileDecisionCache> f, CancellationToken ct) =>
            {
                await Task.Delay(500, ct);
                return new List<AiAnalysisResult>();
            });

        var task = coordinator.AnalyzeBatchAsync(new[] { cache }, CancellationToken.None);

        // Act
        coordinator.CancelCurrentAnalysis();

        // Assert — 任务应该很快完成（被取消）
        var completed = task.Wait(2000);
        Assert.True(completed);
    }
}
