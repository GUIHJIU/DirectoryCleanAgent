using System.Reflection;
using System.Text.Json;
using DirectoryCleanAgent.AI;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.AI;

/// <summary>
/// AiDailyUsageTracker 单元测试 —— 覆盖 JSON 持久化、日期变更重置、
/// 原子写入(.tmp+Move)、损坏文件备份、线程安全。
/// 使用真实临时目录进行文件 I/O 测试。
/// </summary>
public class AiDailyUsageTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger<AiDailyUsageTracker> _logger;

    public AiDailyUsageTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AiUsageTest_{Guid.NewGuid():N}");
        _logger = NullLoggerFactory.Instance.CreateLogger<AiDailyUsageTracker>();
    }

    public void Dispose()
    {
        // 清理临时目录
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* 忽略清理失败 */ }
    }

    /// <summary>创建追踪器实例</summary>
    private AiDailyUsageTracker CreateTracker(int dailyLimit = 50)
    {
        return new AiDailyUsageTracker(_tempDir, dailyLimit, _logger);
    }

    // ============================================================
    // 构造函数
    // ============================================================

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AiDailyUsageTracker(_tempDir, 50, null!));
    }

    [Fact]
    public void Constructor_CreatesDirectoryIfNotExists()
    {
        // _tempDir 在构造函数中还不存在
        Assert.False(Directory.Exists(_tempDir));

        using var tracker = CreateTracker();

        // 构造函数应创建目录
        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public void Constructor_DefaultPath_WhenDataStoragePathEmpty()
    {
        // 空路径 → 回退到 LocalApplicationData/SpaceClear
        using var tracker = new AiDailyUsageTracker("", 50, _logger);

        // 不应抛异常
        Assert.NotNull(tracker);
    }

    // ============================================================
    // LoadAsync —— 各种场景
    // ============================================================

    [Fact]
    public async Task LoadAsync_FileNotExists_InitializesToZero()
    {
        using var tracker = CreateTracker();

        await tracker.LoadAsync();

        Assert.Equal(0, tracker.UsedCount);
        Assert.False(tracker.IsLimitReached);
    }

    [Fact]
    public async Task LoadAsync_LoadsExistingValidFile()
    {
        // 预先写入有效的用量文件
        var filePath = Path.Combine(_tempDir, "ai_usage.json");
        Directory.CreateDirectory(_tempDir);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var json = $"{{\"date\":\"{today}\",\"count\":15}}";
        await File.WriteAllTextAsync(filePath, json);

        using var tracker = CreateTracker();
        await tracker.LoadAsync();

        Assert.Equal(15, tracker.UsedCount);
        Assert.False(tracker.IsLimitReached);
    }

    [Fact]
    public async Task LoadAsync_DateChanged_ResetsCount()
    {
        // 写入昨天的用量文件
        var filePath = Path.Combine(_tempDir, "ai_usage.json");
        Directory.CreateDirectory(_tempDir);
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var json = $"{{\"date\":\"{yesterday}\",\"count\":30}}";
        await File.WriteAllTextAsync(filePath, json);

        using var tracker = CreateTracker();
        await tracker.LoadAsync();

        // 日期不同 → 重置为 0
        Assert.Equal(0, tracker.UsedCount);
    }

    [Fact]
    public async Task LoadAsync_CorruptedJson_BackupsAndResets()
    {
        // 写入损坏的 JSON 文件
        var filePath = Path.Combine(_tempDir, "ai_usage.json");
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(filePath, "这不是有效的JSON{{{");

        using var tracker = CreateTracker();
        await tracker.LoadAsync();

        // 损坏 → 重置为 0
        Assert.Equal(0, tracker.UsedCount);

        // 原始文件已被移走（备份）
        Assert.False(File.Exists(filePath));

        // 备份文件应存在
        var backupFiles = Directory.GetFiles(_tempDir, "ai_usage.json.corrupted.*");
        Assert.NotEmpty(backupFiles);
    }

    [Fact]
    public async Task LoadAsync_EmptyJson_ResetsCount()
    {
        var filePath = Path.Combine(_tempDir, "ai_usage.json");
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(filePath, "null");

        using var tracker = CreateTracker();
        await tracker.LoadAsync();

        // null JSON → 重置为 0
        Assert.Equal(0, tracker.UsedCount);
    }

    [Fact]
    public async Task LoadAsync_CountExceedsDailyLimit_ClampsToLimit()
    {
        // 写入的计数超过日限
        var filePath = Path.Combine(_tempDir, "ai_usage.json");
        Directory.CreateDirectory(_tempDir);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var json = $"{{\"date\":\"{today}\",\"count\":999}}";
        await File.WriteAllTextAsync(filePath, json);

        using var tracker = CreateTracker(50); // 日限 50
        await tracker.LoadAsync();

        // 应被钳位到日限值
        Assert.Equal(50, tracker.UsedCount);
        Assert.True(tracker.IsLimitReached);
    }

    // ============================================================
    // IncrementAsync —— 递增逻辑
    // ============================================================

    [Fact]
    public async Task IncrementAsync_IncrementsAndPersistsToFile()
    {
        using var tracker = CreateTracker();
        await tracker.LoadAsync();
        Assert.Equal(0, tracker.UsedCount);

        var count = await tracker.IncrementAsync();
        Assert.Equal(1, count);
        Assert.Equal(1, tracker.UsedCount);

        // 验证文件已写入
        var filePath = Path.Combine(_tempDir, "ai_usage.json");
        Assert.True(File.Exists(filePath));

        var json = await File.ReadAllTextAsync(filePath);
        Assert.Contains("\"count\":1", json);
    }

    [Fact]
    public async Task IncrementAsync_DateChange_ResetsThenIncrements()
    {
        using var tracker = CreateTracker();
        await tracker.LoadAsync();

        // 先递增一次
        await tracker.IncrementAsync();
        Assert.Equal(1, tracker.UsedCount);

        // 通过反射设置 _currentDate 为昨天，模拟日期变更
        SetCurrentDate(tracker, DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"));

        // 再次递增 → 应检测到日期变更并重置
        var count = await tracker.IncrementAsync();
        Assert.Equal(1, count); // 重置后递增 → 1（不是 2）
        Assert.Equal(1, tracker.UsedCount);
    }

    [Fact]
    public async Task IncrementAsync_AtomicWrite_TempThenMove()
    {
        using var tracker = CreateTracker();
        await tracker.LoadAsync();

        // 递增一次
        await tracker.IncrementAsync();

        // 验证最终文件存在（不验证 .tmp 中间态，因为太快了）
        var filePath = Path.Combine(_tempDir, "ai_usage.json");
        Assert.True(File.Exists(filePath));

        // .tmp 文件不应残留（Move 已替换）
        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public async Task IncrementAsync_MultipleIncrements()
    {
        using var tracker = CreateTracker();
        await tracker.LoadAsync();

        for (var i = 0; i < 10; i++)
        {
            await tracker.IncrementAsync();
        }

        Assert.Equal(10, tracker.UsedCount);

        // 验证文件中的计数
        var filePath = Path.Combine(_tempDir, "ai_usage.json");
        var json = await File.ReadAllTextAsync(filePath);
        Assert.Contains("\"count\":10", json);
    }

    [Fact]
    public async Task IncrementAsync_ExceedsLimit_StillIncrements()
    {
        using var tracker = CreateTracker(5); // 日限 5
        await tracker.LoadAsync();

        // 递增到超过日限
        for (var i = 0; i < 10; i++)
        {
            await tracker.IncrementAsync();
        }

        // 即使超过日限，计数仍然递增（不拒绝，由调用方决定）
        Assert.Equal(10, tracker.UsedCount);
        Assert.True(tracker.IsLimitReached);
    }

    // ============================================================
    // 属性与日限逻辑
    // ============================================================

    [Fact]
    public async Task IsLimitReached_AtLimit_ReturnsTrue()
    {
        using var tracker = CreateTracker(3);
        await tracker.LoadAsync();

        await tracker.IncrementAsync();
        await tracker.IncrementAsync();
        await tracker.IncrementAsync();

        Assert.Equal(3, tracker.UsedCount);
        Assert.True(tracker.IsLimitReached);
    }

    [Fact]
    public async Task RemainingCount_CalculatesCorrectly()
    {
        using var tracker = CreateTracker(50);
        await tracker.LoadAsync();

        Assert.Equal(50, tracker.RemainingCount);

        await tracker.IncrementAsync();
        Assert.Equal(49, tracker.RemainingCount);

        await tracker.IncrementAsync();
        Assert.Equal(48, tracker.RemainingCount);
    }

    // ============================================================
    // IO 异常处理
    // ============================================================

    [Fact]
    public async Task IncrementAsync_WriteFailure_ReturnsInMemoryCount()
    {
        using var tracker = CreateTracker();
        await tracker.LoadAsync();

        // 递增一次使内存计数为 1
        await tracker.IncrementAsync();

        // 删除目录制造写入失败
        Directory.Delete(_tempDir, recursive: true);

        // 再次递增 → 写入文件会失败，但内存计数仍应正确递增
        var count = await tracker.IncrementAsync();
        Assert.Equal(2, count);
        Assert.Equal(2, tracker.UsedCount);
    }

    // ============================================================
    // 线程安全（标注已知问题）
    // ============================================================

    [Fact]
    public async Task UsedCount_ThreadSafety_ConcurrentReadWrite()
    {
        // 注意：UsedCount getter 使用 lock(_lock)（C# Monitor），
        // 而 LoadAsync/IncrementAsync 使用 SemaphoreSlim(1,1)，
        // 两者不互斥 —— 这是一个已知的潜在竞态条件。
        // 本测试验证在正常并发使用下不抛异常。
        using var tracker = CreateTracker();
        await tracker.LoadAsync();

        var tasks = new List<Task>();
        for (var i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await tracker.IncrementAsync();
                var _ = tracker.UsedCount;
                var __ = tracker.IsLimitReached;
            }));
        }

        await Task.WhenAll(tasks);

        // 所有递增操作应完成
        Assert.Equal(20, tracker.UsedCount);
    }

    // ============================================================
    // Dispose
    // ============================================================

    [Fact]
    public void Dispose_Idempotent()
    {
        var tracker = CreateTracker();

        tracker.Dispose();
        var ex = Record.Exception(() => tracker.Dispose());
        Assert.Null(ex);
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    /// <summary>通过反射设置内部 _currentDate 字段（用于日期变更测试）</summary>
    private static void SetCurrentDate(AiDailyUsageTracker tracker, string date)
    {
        var field = typeof(AiDailyUsageTracker).GetField("_currentDate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(tracker, date);
    }
}
