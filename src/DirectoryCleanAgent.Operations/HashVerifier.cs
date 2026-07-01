using System.Security.Cryptography;

namespace DirectoryCleanAgent.Operations;

/// <summary>
/// 文件哈希校验工具。
///
/// 设计原则（设计文档 B4 要求）：
/// - 仅校验已存在的哈希，不重新计算原文件哈希（原文件哈希在 B3 快照生成时已完成）。
/// - 跨卷隔离区复制时，计算目标文件的 SHA-256 并与快照中的哈希比对。
/// - 标准删除路径直接使用快照中的 Sha256Hash，不重复计算。
///
/// 流式读取（4KB 缓冲区），与 B3 DecisionEngine 的 ComputeSha256HashAsync 保持一致。
/// </summary>
internal static class HashVerifier
{
    /// <summary>SHA-256 计算缓冲区大小：4KB</summary>
    private const int BufferSize = 4096;

    /// <summary>
    /// 验证文件 SHA-256 哈希是否与预期值匹配。
    ///
    /// 使用流式 I/O 计算哈希，不会将整个文件加载到内存。
    /// 用于隔离区复制后校验目标文件完整性。
    /// </summary>
    /// <param name="filePath">\\?\ 格式的文件路径</param>
    /// <param name="expectedHash">预期的 SHA-256 哈希（小写十六进制字符串）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>true 表示哈希匹配，false 表示不匹配或文件不存在</returns>
    public static async Task<bool> VerifyHashAsync(
        string filePath,
        string expectedHash,
        CancellationToken ct)
    {
        try
        {
            var actualHash = await ComputeSha256Async(filePath, ct).ConfigureAwait(false);
            if (actualHash == null)
                return false;

            // 大小写不敏感比较（哈希统一小写）
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 流式计算文件的 SHA-256 哈希值。
    ///
    /// 实现细节：
    /// - 使用 FileStream 异步读取，4KB 缓冲区
    /// - 共享读取模式（FileShare.Read），允许其他进程并发读取
    /// - 返回小写十六进制字符串
    /// </summary>
    /// <param name="filePath">\\?\ 格式的文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>小写十六进制 SHA-256 哈希字符串，失败返回 null</returns>
    private static async Task<string?> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        try
        {
            using var sha256 = SHA256.Create();
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: BufferSize,
                useAsync: true);

            var hashBytes = await sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
