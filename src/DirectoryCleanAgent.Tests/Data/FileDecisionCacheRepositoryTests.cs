using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Data;

/// <summary>
/// FileDecisionCacheRepository 单元测试。
/// 验证决策缓存的 Upsert、多维度查询、版本控制及清空操作。
/// </summary>
public class FileDecisionCacheRepositoryTests : IAsyncDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseInitializer _initializer;
    private readonly FileDecisionCacheRepository _repository;
    private readonly ILogger _logger;

    public FileDecisionCacheRepositoryTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"test_fdc_{Guid.NewGuid():N}.db");
        _logger = NullLoggerFactory.Instance.CreateLogger("test");

        _initializer = new DatabaseInitializer(_tempDbPath, _logger);
        _initializer.InitializeAsync().GetAwaiter().GetResult();

        _repository = new FileDecisionCacheRepository(_initializer, _logger);
    }

    public async ValueTask DisposeAsync()
    {
        _repository.Dispose();

        try
        {
            if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath);
            var walPath = _tempDbPath + "-wal";
            var shmPath = _tempDbPath + "-shm";
            if (File.Exists(walPath)) File.Delete(walPath);
            if (File.Exists(shmPath)) File.Delete(shmPath);
        }
        catch { }

        await Task.CompletedTask;
    }

    private static FileDecisionCache CreateTestEntry(
        string filePath = @"\\?\C:\test.log",
        FinalAction finalAction = FinalAction.SuggestDelete,
        string semanticCategory = "系统临时文件",
        int cacheVersion = 1)
    {
        return new FileDecisionCache
        {
            FilePath = filePath,
            SizeBytes = 1024 * 1024,
            LastWriteTime = DateTime.UtcNow,
            RuleVerdict = RuleVerdict.SuggestDelete,
            SemanticCategory = semanticCategory,
            FinalAction = finalAction,
            CacheVersion = cacheVersion,
        };
    }

    [Fact]
    public async Task Upsert_And_GetByFilePath_ShouldPersistAndRetrieve()
    {
        // Arrange
        var entry = CreateTestEntry();

        // Act
        _repository.Upsert(entry);
        await _repository.FlushAsync();

        var retrieved = await _repository.GetByFilePathAsync(entry.FilePath);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(entry.FilePath, retrieved!.FilePath);
        Assert.Equal(entry.SizeBytes, retrieved.SizeBytes);
        Assert.Equal(entry.FinalAction, retrieved.FinalAction);
        Assert.Equal(entry.SemanticCategory, retrieved.SemanticCategory);
    }

    [Fact]
    public async Task Upsert_DuplicateFilePath_ShouldReplaceExisting()
    {
        // Arrange
        var original = CreateTestEntry(filePath: @"\\?\C:\dup.log", cacheVersion: 1);
        _repository.Upsert(original);
        await _repository.FlushAsync();

        // Act — 用新版本号更新同一路径
        var updated = CreateTestEntry(filePath: @"\\?\C:\dup.log", cacheVersion: 2);
        _repository.Upsert(updated);
        await _repository.FlushAsync();

        var retrieved = await _repository.GetByFilePathAsync(@"\\?\C:\dup.log");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(2, retrieved!.CacheVersion);
    }

    [Fact]
    public async Task GetByAction_ShouldReturnMatchingEntries()
    {
        // Arrange
        _repository.Upsert(CreateTestEntry(@"\\?\C:\auto1.log", FinalAction.AutoDelete));
        _repository.Upsert(CreateTestEntry(@"\\?\C:\auto2.log", FinalAction.AutoDelete));
        _repository.Upsert(CreateTestEntry(@"\\?\C:\suggest.log", FinalAction.SuggestDelete));
        _repository.Upsert(CreateTestEntry(@"\\?\C:\review.log", FinalAction.ManualReview));
        _repository.Upsert(CreateTestEntry(@"\\?\C:\protected.log", FinalAction.Protected));
        await _repository.FlushAsync();

        // Act
        var autoDeletes = await _repository.GetByActionAsync(FinalAction.AutoDelete);
        var suggests = await _repository.GetByActionAsync(FinalAction.SuggestDelete);
        var protecteds = await _repository.GetByActionAsync(FinalAction.Protected);

        // Assert
        Assert.Equal(2, autoDeletes.Count);
        Assert.Single(suggests);
        Assert.Single(protecteds);
    }

    [Fact]
    public async Task GetBySemanticCategory_ShouldFilterCorrectly()
    {
        // Arrange
        _repository.Upsert(CreateTestEntry(@"\\?\C:\temp.log", semanticCategory: "系统临时文件"));
        _repository.Upsert(CreateTestEntry(@"\\?\C:\cache.tmp", semanticCategory: "应用缓存"));
        _repository.Upsert(CreateTestEntry(@"\\?\C:\temp2.log", semanticCategory: "系统临时文件"));
        await _repository.FlushAsync();

        // Act
        var tempFiles = await _repository.GetBySemanticCategoryAsync("系统临时文件");
        var cacheFiles = await _repository.GetBySemanticCategoryAsync("应用缓存");

        // Assert
        Assert.Equal(2, tempFiles.Count);
        Assert.Single(cacheFiles);
    }

    [Fact]
    public async Task GetByVersion_ShouldReturnMatchingEntries()
    {
        // Arrange
        _repository.Upsert(CreateTestEntry(@"\\?\C:\v1.log", cacheVersion: 1));
        _repository.Upsert(CreateTestEntry(@"\\?\C:\v2.log", cacheVersion: 2));
        _repository.Upsert(CreateTestEntry(@"\\?\C:\v2b.log", cacheVersion: 2));
        await _repository.FlushAsync();

        // Act
        var v1Entries = await _repository.GetByVersionAsync(1);
        var v2Entries = await _repository.GetByVersionAsync(2);

        // Assert
        Assert.Single(v1Entries);
        Assert.Equal(2, v2Entries.Count);
    }

    [Fact]
    public async Task ClearAsync_ShouldRemoveAllEntries()
    {
        // Arrange
        _repository.Upsert(CreateTestEntry(@"\\?\C:\f1.log"));
        _repository.Upsert(CreateTestEntry(@"\\?\C:\f2.log"));
        _repository.Upsert(CreateTestEntry(@"\\?\C:\f3.log"));
        await _repository.FlushAsync();

        // Act
        await _repository.ClearAsync();

        var all = await _repository.GetAllAsync();

        // Assert
        Assert.Empty(all);
    }

    [Fact]
    public async Task UpsertBatch_ShouldPersistAllEntries()
    {
        // Arrange
        var entries = new[]
        {
            CreateTestEntry(@"\\?\C:\batch1.log"),
            CreateTestEntry(@"\\?\C:\batch2.log"),
            CreateTestEntry(@"\\?\C:\batch3.log"),
            CreateTestEntry(@"\\?\C:\batch4.log"),
            CreateTestEntry(@"\\?\C:\batch5.log"),
        };

        // Act
        _repository.UpsertBatch(entries);
        await _repository.FlushAsync();

        var all = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(5, all.Count);
    }

    [Fact]
    public async Task AiLabel_ShouldBePersistedAsNullable()
    {
        // Arrange
        var withAi = CreateTestEntry(@"\\?\C:\ai.log");
        withAi.AiLabel = "safe";
        withAi.AiConfidence = 0.95;
        withAi.AiExplanation = "该文件匹配已知安全模式";

        _repository.Upsert(withAi);
        await _repository.FlushAsync();

        // Act
        var retrieved = await _repository.GetByFilePathAsync(@"\\?\C:\ai.log");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("safe", retrieved!.AiLabel);
        Assert.Equal(0.95, retrieved.AiConfidence);
        Assert.Equal("该文件匹配已知安全模式", retrieved.AiExplanation);
    }

    [Fact]
    public async Task UserDecision_ShouldBePersistedAsNullableEnum()
    {
        // Arrange
        var withDecision = CreateTestEntry(@"\\?\C:\user-decision.log");
        withDecision.UserDecision = FinalAction.AutoDelete;

        _repository.Upsert(withDecision);
        await _repository.FlushAsync();

        // Act
        var retrieved = await _repository.GetByFilePathAsync(@"\\?\C:\user-decision.log");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(FinalAction.AutoDelete, retrieved!.UserDecision);
    }
}
