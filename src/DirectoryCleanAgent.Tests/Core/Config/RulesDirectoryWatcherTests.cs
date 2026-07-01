using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Core.Config;

/// <summary>
/// RulesDirectoryWatcher 防抖与生命周期测试。
///
/// 使用内嵌的 ConfigServiceSpy 替代 Moq，避免额外 NuGet 依赖。
/// 所有测试使用临时目录，不污染真实规则目录。
/// </summary>
public class RulesDirectoryWatcherTests : IDisposable
{
    private readonly string _tempRoot;

    public RulesDirectoryWatcherTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"DCA_WatcherTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* 清理失败不阻塞测试 */ }
    }

    // ============================================================
    // 测试 1：目录自动创建
    // ============================================================

    [Fact]
    public async Task StartAsync_RulesDirectoryNotExists_CreatesDirectory()
    {
        // Arrange
        var spy = new ConfigServiceSpy();
        var logger = NullLogger<RulesDirectoryWatcher>.Instance;
        var rulesDir = Path.Combine(_tempRoot, "rules_auto_create");

        // 确保目录不存在
        Assert.False(Directory.Exists(rulesDir));

        using var watcher = new RulesDirectoryWatcher(spy, logger)
        {
            RulesDirectoryPath = rulesDir,
            DebounceIntervalMs = 100
        };

        // Act
        await watcher.StartAsync();

        // Assert — 目录应被自动创建
        Assert.True(Directory.Exists(rulesDir));
    }

    // ============================================================
    // 测试 2：JSON 文件变更触发版本递增（防抖后）
    // ============================================================

    [Fact]
    public async Task FileChanged_AfterDebounce_IncrementsVersion()
    {
        // Arrange
        var spy = new ConfigServiceSpy();
        var logger = NullLogger<RulesDirectoryWatcher>.Instance;
        var rulesDir = Path.Combine(_tempRoot, "rules_test");
        Directory.CreateDirectory(rulesDir);

        using var watcher = new RulesDirectoryWatcher(spy, logger)
        {
            RulesDirectoryPath = rulesDir,
            DebounceIntervalMs = 100
        };
        await watcher.StartAsync();

        // Act — 创建一个 JSON 规则文件
        var ruleFile = Path.Combine(rulesDir, "test_rule.json");
        await File.WriteAllTextAsync(ruleFile, "{}");

        // 等待防抖 + 异步保存完成
        await Task.Delay(800);

        // Assert — IncrementRuleCacheVersion 被调用
        Assert.True(spy.IncrementCallCount > 0,
            $"防抖后应触发版本递增，实际调用次数: {spy.IncrementCallCount}");
    }

    // ============================================================
    // 测试 3：多次快速变更合并为一次防抖
    // ============================================================

    [Fact]
    public async Task MultipleRapidChanges_DebouncedToMinimumCalls()
    {
        // Arrange
        var spy = new ConfigServiceSpy();
        var logger = NullLogger<RulesDirectoryWatcher>.Instance;
        var rulesDir = Path.Combine(_tempRoot, "rules_debounce");
        Directory.CreateDirectory(rulesDir);

        using var watcher = new RulesDirectoryWatcher(spy, logger)
        {
            RulesDirectoryPath = rulesDir,
            DebounceIntervalMs = 300
        };
        await watcher.StartAsync();

        // Act — 快速连续创建 5 个文件（间隔 20ms）
        for (int i = 0; i < 5; i++)
        {
            var ruleFile = Path.Combine(rulesDir, $"rule_{i}.json");
            await File.WriteAllTextAsync(ruleFile, "{}");
            await Task.Delay(20);
        }

        // 等待防抖周期 + 余量
        await Task.Delay(1000);

        // Assert — 5 次快速变更应被防抖合并，调用次数远小于 5
        Assert.True(spy.IncrementCallCount > 0,
            "至少应触发一次版本递增");
        Assert.True(spy.IncrementCallCount < 5,
            $"防抖应合并连续变更，实际调用次数: {spy.IncrementCallCount}，预期 < 5");
    }

    // ============================================================
    // 测试 4：非 JSON 文件不触发版本递增
    // ============================================================

    [Fact]
    public async Task NonJsonFile_DoesNotTriggerIncrement()
    {
        // Arrange
        var spy = new ConfigServiceSpy();
        var logger = NullLogger<RulesDirectoryWatcher>.Instance;
        var rulesDir = Path.Combine(_tempRoot, "rules_filter");
        Directory.CreateDirectory(rulesDir);

        using var watcher = new RulesDirectoryWatcher(spy, logger)
        {
            RulesDirectoryPath = rulesDir,
            DebounceIntervalMs = 100
        };
        await watcher.StartAsync();

        var beforeCount = spy.IncrementCallCount;

        // Act — 创建非 .json 文件
        var txtFile = Path.Combine(rulesDir, "note.txt");
        await File.WriteAllTextAsync(txtFile, "hello");
        await Task.Delay(500);

        // Assert — 不应触发版本递增
        Assert.Equal(beforeCount, spy.IncrementCallCount);
    }

    // ============================================================
    // 测试 5：StopAsync 后不再响应文件变更
    // ============================================================

    [Fact]
    public async Task StopAsync_StopsMonitoring()
    {
        // Arrange
        var spy = new ConfigServiceSpy();
        var logger = NullLogger<RulesDirectoryWatcher>.Instance;
        var rulesDir = Path.Combine(_tempRoot, "rules_stop");
        Directory.CreateDirectory(rulesDir);

        using var watcher = new RulesDirectoryWatcher(spy, logger)
        {
            RulesDirectoryPath = rulesDir,
            DebounceIntervalMs = 100
        };
        await watcher.StartAsync();
        await watcher.StopAsync();

        var beforeCount = spy.IncrementCallCount;

        // Act — 停止后创建文件
        var ruleFile = Path.Combine(rulesDir, "after_stop.json");
        await File.WriteAllTextAsync(ruleFile, "{}");
        await Task.Delay(500);

        // Assert — 不应触发新的版本递增
        Assert.Equal(beforeCount, spy.IncrementCallCount);
    }

    // ============================================================
    // 测试 6：Start → Stop → Start 可重启
    // ============================================================

    [Fact]
    public async Task Restart_AfterStop_ResumesMonitoring()
    {
        // Arrange
        var spy = new ConfigServiceSpy();
        var logger = NullLogger<RulesDirectoryWatcher>.Instance;
        var rulesDir = Path.Combine(_tempRoot, "rules_restart");
        Directory.CreateDirectory(rulesDir);

        using var watcher = new RulesDirectoryWatcher(spy, logger)
        {
            RulesDirectoryPath = rulesDir,
            DebounceIntervalMs = 100
        };

        // Act — 第一次启动 → 停止
        await watcher.StartAsync();
        await watcher.StopAsync();

        // 重新启动
        await watcher.StartAsync();

        var beforeCount = spy.IncrementCallCount;

        var ruleFile = Path.Combine(rulesDir, "after_restart.json");
        await File.WriteAllTextAsync(ruleFile, "{}");
        await Task.Delay(500);

        // Assert — 重启后应能检测到变更
        Assert.True(spy.IncrementCallCount > beforeCount,
            $"重启后应检测到文件变更，调用次数: {spy.IncrementCallCount} (之前: {beforeCount})");
    }

    // ============================================================
    // 测试 7：Dispose 后 IsRunning 为 false
    // ============================================================

    [Fact]
    public async Task Dispose_CleansUpAndStopsRunning()
    {
        // Arrange
        var spy = new ConfigServiceSpy();
        var logger = NullLogger<RulesDirectoryWatcher>.Instance;
        var rulesDir = Path.Combine(_tempRoot, "rules_dispose");
        Directory.CreateDirectory(rulesDir);

        var watcher = new RulesDirectoryWatcher(spy, logger)
        {
            RulesDirectoryPath = rulesDir,
            DebounceIntervalMs = 100
        };
        await watcher.StartAsync();

        // Act
        watcher.Dispose();

        // Assert
        Assert.False(watcher.IsRunning);
    }

    // ============================================================
    // 测试 8：Dispose 后可安全再次调用 Dispose
    // ============================================================

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var spy = new ConfigServiceSpy();
        var logger = NullLogger<RulesDirectoryWatcher>.Instance;
        var watcher = new RulesDirectoryWatcher(spy, logger);

        // Act & Assert — 不抛出异常
        watcher.Dispose();
        var ex = Record.Exception(() => watcher.Dispose());
        Assert.Null(ex);
    }

    // ============================================================
    // 测试 9：运行中修改路径抛出异常
    // ============================================================

    [Fact]
    public async Task ChangePath_WhileRunning_ThrowsInvalidOperationException()
    {
        // Arrange
        var spy = new ConfigServiceSpy();
        var logger = NullLogger<RulesDirectoryWatcher>.Instance;
        var rulesDir = Path.Combine(_tempRoot, "rules_pathchange");
        Directory.CreateDirectory(rulesDir);

        using var watcher = new RulesDirectoryWatcher(spy, logger)
        {
            RulesDirectoryPath = rulesDir,
            DebounceIntervalMs = 100
        };
        await watcher.StartAsync();

        // Act & Assert — 运行中修改路径应抛出
        Assert.Throws<InvalidOperationException>(() =>
        {
            watcher.RulesDirectoryPath = Path.Combine(_tempRoot, "other_dir");
        });
    }

    // ============================================================
    // 内嵌 ConfigServiceSpy：轻量级 IConfigService 测试替身
    // ============================================================

    /// <summary>
    /// 用于测试的 IConfigService 间谍实现。
    /// 只记录 IncrementRuleCacheVersion 调用次数，其余方法抛 NotSupportedException。
    /// </summary>
    private sealed class ConfigServiceSpy : IConfigService
    {
        private int _incrementCallCount;

        /// <summary>IncrementRuleCacheVersion 被调用的总次数</summary>
        public int IncrementCallCount => Volatile.Read(ref _incrementCallCount);

        public UserConfig Current { get; } = new();

        public string ConfigFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpaceClear",
                "config.json");

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void IncrementRuleCacheVersion()
        {
            Interlocked.Increment(ref _incrementCallCount);
            // 递增内存中的版本号以模拟真实行为
            Current.RuleCacheVersion++;
        }

        public event EventHandler? ConfigChanged;
    }
}
