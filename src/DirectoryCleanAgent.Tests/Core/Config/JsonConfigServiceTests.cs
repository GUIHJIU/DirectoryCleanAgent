using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Core.Config;

/// <summary>
/// JsonConfigService 读写及原子写入测试。
/// 使用临时目录避免污染真实配置。
/// </summary>
public class JsonConfigServiceTests : IDisposable
{
    private readonly string _tempDir;

    public JsonConfigServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DCA_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* 清理失败不阻塞测试 */ }
    }

    [Fact]
    public async Task LoadAsync_ConfigFileNotExists_CreatesDefaultConfig()
    {
        // Arrange
        var logger = NullLogger<JsonConfigService>.Instance;
        var service = new JsonConfigService(logger, _tempDir);

        // Act
        await service.LoadAsync();

        // Assert — 配置文件应被自动创建
        Assert.True(File.Exists(service.ConfigFilePath));

        // Assert — 当前配置有默认值
        Assert.Equal("zh-CN", service.Current.Language);
        Assert.Equal(StartupBehavior.AutoScan, service.Current.StartupBehavior);
    }

    [Fact]
    public async Task SaveAsync_WritesAndCanBeReadBack()
    {
        // Arrange
        var logger = NullLogger<JsonConfigService>.Instance;
        var service = new JsonConfigService(logger, _tempDir);

        service.Current.Language = "en-US";
        service.Current.Theme = ThemeType.Dark;
        service.Current.RuleCacheVersion = 42;

        // Act
        await service.SaveAsync();

        // Assert — 文件存在
        Assert.True(File.Exists(service.ConfigFilePath));

        // Act — 用新实例加载
        var service2 = new JsonConfigService(logger, _tempDir);
        await service2.LoadAsync();

        // Assert — 值被正确恢复
        Assert.Equal("en-US", service2.Current.Language);
        Assert.Equal(ThemeType.Dark, service2.Current.Theme);
        Assert.Equal(42, service2.Current.RuleCacheVersion);
    }

    [Fact]
    public async Task LoadAsync_ConfigFileIsCorrupt_FallsBackToDefault()
    {
        // Arrange
        var logger = NullLogger<JsonConfigService>.Instance;

        // 写入损坏的 JSON
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, JsonConfigService.ConfigFileName),
            "this is not valid json {{{");

        var service = new JsonConfigService(logger, _tempDir);

        // Act — 不应抛出异常
        await service.LoadAsync();

        // Assert — 回退为默认配置
        Assert.Equal("zh-CN", service.Current.Language);
    }

    [Fact]
    public async Task IncrementRuleCacheVersion_ReturnsIncrementedValue()
    {
        // Arrange
        var logger = NullLogger<JsonConfigService>.Instance;
        var service = new JsonConfigService(logger, _tempDir);
        service.Current.RuleCacheVersion = 10;

        // 使用 TaskCompletionSource 等待异步保存完成
        var tcs = new TaskCompletionSource<bool>();
        service.ConfigChanged += (_, _) => tcs.TrySetResult(true);

        // Act
        service.IncrementRuleCacheVersion();

        // 等待异步操作完成（递增+保存+事件在 Task.Run 内执行）
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Assert — 版本号递增
        Assert.Equal(11, service.Current.RuleCacheVersion);
    }

    [Fact]
    public async Task SaveAsync_UsesAtomicWrite_NoTmpFileLeftBehind()
    {
        // Arrange
        var logger = NullLogger<JsonConfigService>.Instance;
        var service = new JsonConfigService(logger, _tempDir);

        // Act
        await service.SaveAsync();

        // Assert — 临时文件已被清理
        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(tmpFiles);

        // Assert — 只有 config.json 存在
        Assert.True(File.Exists(service.ConfigFilePath));
    }

    [Fact]
    public async Task ConfigChanged_EventFires_AfterSave()
    {
        // Arrange
        var logger = NullLogger<JsonConfigService>.Instance;
        var service = new JsonConfigService(logger, _tempDir);
        var eventFired = false;
        service.ConfigChanged += (_, _) => eventFired = true;

        // Act
        await service.SaveAsync();

        // Assert
        Assert.True(eventFired);
    }

    [Fact]
    public async Task IncrementRuleCacheVersion_WithMultipleConcurrentCalls_AllVersionsIncremented()
    {
        // 验证：多次并发调用 IncrementRuleCacheVersion 不会因竞态条件丢失递增
        // Arrange
        var logger = NullLogger<JsonConfigService>.Instance;
        var service = new JsonConfigService(logger, _tempDir);
        await service.LoadAsync();
        service.Current.RuleCacheVersion = 0;

        // 使用 TaskCompletionSource 等待 ConfigChanged 事件全部触发
        var firedCount = 0;
        var tcs = new TaskCompletionSource<bool>();
        service.ConfigChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref firedCount) >= 10)
                tcs.TrySetResult(true);
        };

        // Act — 模拟 10 次并发 FSW 回调
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => service.IncrementRuleCacheVersion()))
            .ToArray();
        await Task.WhenAll(tasks);

        // 等待所有异步保存完成（含 ConfigChanged 触发）
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 重新加载验证持久化结果
        var service2 = new JsonConfigService(logger, _tempDir);
        await service2.LoadAsync();
        // 10 次并发递增，最终版本号应为 10
        Assert.Equal(10, service2.Current.RuleCacheVersion);
    }

    [Fact]
    public async Task IncrementRuleCacheVersion_ConfigChangedFiresAfterSave()
    {
        // 验证：IncrementRuleCacheVersion 触发 ConfigChanged 事件（在锁外）
        // Arrange
        var logger = NullLogger<JsonConfigService>.Instance;
        var service = new JsonConfigService(logger, _tempDir);
        await service.LoadAsync();

        var tcs = new TaskCompletionSource<bool>();
        service.ConfigChanged += (_, _) => tcs.TrySetResult(true);

        // Act
        service.IncrementRuleCacheVersion();

        // Assert — 事件应在合理时间内触发
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(tcs.Task.IsCompletedSuccessfully);
    }
}
