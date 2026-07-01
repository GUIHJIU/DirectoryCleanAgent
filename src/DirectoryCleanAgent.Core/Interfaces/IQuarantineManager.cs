using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Core.Interfaces;

/// <summary>
/// 隔离区管理器接口 — B6 阶段核心组件。
///
/// 职责范围（设计文档 V3.7 第 7.4 节）：
/// 1. 隔离区目录路径解析与自动创建
/// 2. 隔离区空间使用监控与统计
/// 3. 容量阈值强制检查（QuarantineMaxSizeBytes）
/// 4. 大文件绕过判断（隔离区卷剩余空间 20% 阈值）
/// 5. 过期文件自动清理（QuarantineRetentionDays）
/// 6. 隔离区文件命名规范管理
/// 7. 批量全量清空操作
///
/// 隔离区文件命名规范（B4/B5/B6 共享契约）：
/// {SHA256前16位}_{原始文件名}_{删除时间戳Unix秒}.quarantine
/// 示例：A1B2C3D4E5F6A7B8_项目文档.docx_1718845200.quarantine
/// </summary>
public interface IQuarantineManager
{
    /// <summary>
    /// 获取隔离区目录路径（\\?\ 格式）。
    /// 若 QuarantineMaxSizeBytes ≤ 0（禁用）返回 null。
    /// 目录不存在时自动创建（幂等安全）。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>标准化的隔离区目录路径，或 null</returns>
    Task<string?> GetQuarantineDirectoryAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取隔离区当前使用统计信息。
    /// 枚举隔离区中所有 .quarantine 文件，计算总大小、文件数、
    /// 过期文件数、最旧文件时间戳等。
    /// 禁用时返回 IsDisabled=true 的空统计。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    Task<QuarantineStats> GetQuarantineStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// 判断隔离区是否有足够空间容纳指定大小的单个文件。
    /// 当前已用空间 + fileSize ≤ QuarantineMaxSizeBytes。
    /// 隔离区禁用时返回 false。
    /// </summary>
    /// <param name="fileSize">待添加文件的大小（字节）</param>
    /// <param name="ct">取消令牌</param>
    Task<bool> CanAccommodateAsync(long fileSize, CancellationToken ct = default);

    /// <summary>
    /// 判断文件是否过大而应绕过隔离区直接删除。
    /// 阈值：文件大小 > 隔离区所在卷剩余空间的 20%。
    /// 隔离区禁用或目录不存在时保守返回 true。
    /// </summary>
    /// <param name="fileSize">文件大小（字节）</param>
    /// <param name="ct">取消令牌</param>
    Task<bool> IsLargeFileForQuarantineAsync(long fileSize, CancellationToken ct = default);

    /// <summary>
    /// 容量强制检查：在当前已用空间基础上加上 additionalBytes 是否超出配额。
    /// 超出时记录警告日志并返回 false，由调用方决定降级策略（如绕过隔离区）。
    /// </summary>
    /// <param name="additionalBytes">本次操作计划添加的总字节数</param>
    /// <param name="ct">取消令牌</param>
    Task<bool> EnforceCapacityAsync(long additionalBytes, CancellationToken ct = default);

    /// <summary>
    /// 清理超过 QuarantineRetentionDays 的过期隔离区文件。
    ///
    /// 通过解析文件名中的 Unix 时间戳判断过期（而非文件系统时间戳，
    /// 因为复制/移动可能改变文件的 LastWriteTime）。
    /// 无法解析时间戳的文件将被跳过（安全保守策略）。
    /// 每个文件删除后报告一次进度。
    /// </summary>
    /// <param name="progress">进度报告，为 null 时不报告</param>
    /// <param name="ct">取消令牌，触发后已删除的文件不回滚</param>
    Task<QuarantineCleanupResult> CleanupExpiredAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// 清空隔离区中的所有 .quarantine 文件。
    /// 仅删除 .quarantine 扩展名的文件，不影响隔离区目录中其他文件。
    /// 每个文件删除后报告一次进度。
    /// </summary>
    /// <param name="progress">进度报告，为 null 时不报告</param>
    /// <param name="ct">取消令牌，触发后已删除的文件不回滚</param>
    Task<QuarantineCleanupResult> PurgeAllAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// 构造符合隔离区命名规范的文件名（不含路径）。
    /// 格式：{SHA256前16位}_{原始文件名}_{当前Unix秒}.quarantine
    /// </summary>
    /// <param name="sha256Hash">完整 64 字符十六进制 SHA-256 哈希</param>
    /// <param name="originalFileName">原始文件名（含扩展名）</param>
    /// <returns>符合命名规范的隔离区文件名</returns>
    string ConstructQuarantineFileName(string sha256Hash, string originalFileName);

    /// <summary>
    /// 解析隔离区文件名，提取 SHA256 前缀、原始文件名和 Unix 时间戳。
    /// 用于恢复时的文件匹配和过期清理时的时间戳提取。
    ///
    /// 文件名格式：{SHA256}_{OriginalFileName}_{UnixSeconds}.quarantine
    /// 因原始文件名中可能含下划线，使用首下划线和末下划线定位三部分。
    /// </summary>
    /// <param name="fileName">符合 ConstructQuarantineFileName 产出的文件名</param>
    /// <returns>
    /// 解析成功返回 (Sha256Prefix, OriginalFileName, UnixSeconds)；
    /// 格式不合法返回 null
    /// </returns>
    (string Sha256Prefix, string OriginalFileName, long UnixSeconds)? ParseQuarantineFileName(
        string fileName);

    /// <summary>
    /// 枚举隔离区中的所有 .quarantine 文件，解析文件名元数据，
    /// 返回结构化的展示条目列表。用于 C7 隔离区管理界面的文件列表展示。
    ///
    /// 每个文件通过 ParseQuarantineFileName 解析文件名中的原始文件名、
    /// SHA256 前缀和隔离时间戳，并结合文件系统信息（大小）和配置（保留天数）
    /// 构建完整的 QuarantineFileEntry。
    ///
    /// 单个文件解析失败时记录警告日志并跳过，不中断整体枚举。
    /// 隔离区禁用或目录不存在时返回空列表。
    /// </summary>
    /// <param name="progress">进度报告（每处理一个文件报告一次），为 null 时不报告</param>
    /// <param name="ct">取消令牌，触发后已读取的条目不回滚</param>
    /// <returns>隔离区文件条目列表（按文件名排序）</returns>
    Task<IReadOnlyList<QuarantineFileEntry>> ListQuarantineFilesAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// 从隔离区永久删除单个 .quarantine 文件。
    /// 仅在隔离区管理界面中使用，不触发墓碑清理或审计日志。
    /// 文件不存在时视为成功（幂等语义）。
    /// </summary>
    /// <param name="quarantineFilePath">要删除的隔离区文件完整路径（\\?\ 格式）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>true 表示删除成功（文件不存在也返回 true）</returns>
    Task<bool> DeleteQuarantineFileAsync(
        string quarantineFilePath,
        CancellationToken ct = default);
}
