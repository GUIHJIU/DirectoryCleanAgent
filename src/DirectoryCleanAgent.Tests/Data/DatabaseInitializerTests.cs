using DirectoryCleanAgent.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Data;

/// <summary>
/// DatabaseInitializer 单元测试。
/// 验证数据库文件创建、WAL 模式设置、表结构和索引的完整性。
/// 使用临时文件数据库，测试结束后自动清理。
/// </summary>
public class DatabaseInitializerTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly ILogger _logger;

    public DatabaseInitializerTests()
    {
        // 每次测试使用独立的临时数据库文件
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"test_a3_{Guid.NewGuid():N}.db");
        _logger = NullLoggerFactory.Instance.CreateLogger("test");
    }

    public void Dispose()
    {
        // 清理临时数据库文件
        try
        {
            if (File.Exists(_tempDbPath))
                File.Delete(_tempDbPath);

            // 同时清理 WAL/SHM 文件
            var walPath = _tempDbPath + "-wal";
            var shmPath = _tempDbPath + "-shm";
            if (File.Exists(walPath)) File.Delete(walPath);
            if (File.Exists(shmPath)) File.Delete(shmPath);
        }
        catch
        {
            // 清理失败不阻塞测试
        }
    }

    [Fact]
    public async Task InitializeAsync_ShouldCreateDatabaseFile()
    {
        // Arrange
        var initializer = new DatabaseInitializer(_tempDbPath, _logger);

        // Act
        await initializer.InitializeAsync();

        // Assert
        Assert.True(File.Exists(_tempDbPath), "数据库文件应被创建");
        Assert.True(new FileInfo(_tempDbPath).Length > 0, "数据库文件不应为空");
    }

    [Fact]
    public async Task InitializeAsync_ShouldEnableWalMode()
    {
        // Arrange
        var initializer = new DatabaseInitializer(_tempDbPath, _logger);
        await initializer.InitializeAsync();

        // Act
        await using var connection = await initializer.CreateConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var journalMode = (await cmd.ExecuteScalarAsync()) as string;

        // Assert
        Assert.Equal("wal", journalMode?.ToLower());
    }

    [Fact]
    public async Task InitializeAsync_ShouldSetSynchronousNormal()
    {
        // Arrange
        var initializer = new DatabaseInitializer(_tempDbPath, _logger);
        await initializer.InitializeAsync();

        // Act
        await using var connection = await initializer.CreateConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous;";
        var syncMode = Convert.ToInt32(await cmd.ExecuteScalarAsync()!);

        // Assert
        Assert.Equal(1, syncMode); // NORMAL = 1
    }

    [Fact]
    public async Task InitializeAsync_ShouldBeIdempotent()
    {
        // Arrange
        var initializer = new DatabaseInitializer(_tempDbPath, _logger);

        // Act — 多次调用不应抛出异常
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        // Assert — 数据库文件仍存在
        Assert.True(File.Exists(_tempDbPath));
    }

    [Fact]
    public async Task InitializeAsync_ShouldCreateAllFourTables()
    {
        // Arrange
        var initializer = new DatabaseInitializer(_tempDbPath, _logger);
        await initializer.InitializeAsync();

        // Act
        await using var connection = await initializer.CreateConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;

        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        // Assert
        Assert.Contains("AuditLog", tables);
        Assert.Contains("DeletionRecord", tables);
        Assert.Contains("FileDecisionCache", tables);
        Assert.Contains("LocalTombstone", tables);
    }

    [Theory]
    [InlineData("FileDecisionCache", "file_path")]
    [InlineData("FileDecisionCache", "size_bytes")]
    [InlineData("FileDecisionCache", "rule_verdict")]
    [InlineData("FileDecisionCache", "semantic_category")]
    [InlineData("FileDecisionCache", "cache_version")]
    [InlineData("LocalTombstone", "file_identity_key")]
    [InlineData("LocalTombstone", "operation_id")]
    [InlineData("LocalTombstone", "deleted_at")]
    [InlineData("DeletionRecord", "operation_id")]
    [InlineData("DeletionRecord", "file_hash")]
    [InlineData("DeletionRecord", "decision_snapshot")]
    [InlineData("AuditLog", "timestamp")]
    [InlineData("AuditLog", "user_sid")]
    [InlineData("AuditLog", "operation_type")]
    public async Task InitializeAsync_TableShouldHaveColumn(string tableName, string columnName)
    {
        // Arrange
        var initializer = new DatabaseInitializer(_tempDbPath, _logger);
        await initializer.InitializeAsync();

        // Act
        await using var connection = await initializer.CreateConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        cmd.Parameters.AddWithValue("@table", tableName);

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1)); // name 列
        }

        // Assert
        Assert.Contains(columnName, columns);
    }

    [Fact]
    public async Task CreateConnectionAsync_ShouldReturnOpenConnection()
    {
        // Arrange
        var initializer = new DatabaseInitializer(_tempDbPath, _logger);
        await initializer.InitializeAsync();

        // Act
        await using var connection = await initializer.CreateConnectionAsync();

        // Assert
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }
}
