using Microsoft.Data.Sqlite;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// SQLite 连接工厂接口。
/// 每次调用创建并打开一个新的 SqliteConnection，WAL 模式下支持并发读写。
/// 由 DatabaseInitializer 初始化后注册到 DI 容器。
/// </summary>
public interface ISqliteConnectionFactory
{
    /// <summary>创建并打开一个新的数据库连接（已调用 OpenAsync）</summary>
    Task<SqliteConnection> CreateConnectionAsync(CancellationToken ct = default);

    /// <summary>获取数据库文件路径</summary>
    string DatabasePath { get; }
}
