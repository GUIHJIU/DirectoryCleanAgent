// A3 阶段已实现，此文件保留为模块标识。
// 实际实现请参见以下文件：
// - DatabaseInitializer.cs       — SQLite 数据库初始化（建表/WAL/索引）
// - BatchWriteQueue.cs           — 通用批量写入队列（ConcurrentQueue, 500ms/200条）
// - FileDecisionCacheRepository.cs — 决策缓存仓储
// - LocalTombstoneRepository.cs    — 墓碑仓储
// - DeletionRecordRepository.cs    — 删除记录仓储
// - AuditLogRepository.cs          — 审计日志仓储（仅 Insert）
// - ServiceRegistration.cs         — DI 注册扩展

namespace DirectoryCleanAgent.Data;

internal static class Placeholder
{
    internal static string ModuleName => nameof(DirectoryCleanAgent.Data);
    internal static string Version => "A3";
}
