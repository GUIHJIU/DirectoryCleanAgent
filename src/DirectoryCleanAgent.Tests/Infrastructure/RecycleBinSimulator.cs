// ============================================================
// RecycleBinSimulator.cs
// 功能: 回收站文件模拟器
// 在集成测试中模拟回收站中的 $R* 文件结构
// 与 BackupManager.TryRestoreFromRecycleBinAsync 的搜索模式保持一致
// ============================================================

using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DirectoryCleanAgent.Tests.Infrastructure;

/// <summary>
/// 回收站模拟器 — 模拟 Windows $Recycle.Bin\{SID}\$R* 文件结构
/// 不依赖真实回收站 API，用于集成测试中验证回滚和恢复逻辑
/// </summary>
public sealed class RecycleBinSimulator : IDisposable
{
    private readonly string _recycleBinDir;
    private readonly List<string> _createdFiles = new();
    private bool _disposed;

    /// <summary>
    /// 回收站目录路径（格式: X:\$Recycle.Bin\{SID}）
    /// </summary>
    public string RecycleBinDirectory => _recycleBinDir;

    /// <summary>
    /// 构造回收站模拟器
    /// </summary>
    /// <param name="driveRoot">驱动器根目录（如 "D:\TestVolume"），$Recycle.Bin 将创建在其下</param>
    /// <param name="sid">用户 SID，默认使用当前用户 SID</param>
    public RecycleBinSimulator(string driveRoot, string? sid = null)
    {
        sid ??= GetCurrentUserSid();
        _recycleBinDir = Path.Combine(driveRoot, "$Recycle.Bin", sid);
        if (!Directory.Exists(_recycleBinDir))
        {
            Directory.CreateDirectory(_recycleBinDir);
        }
    }

    // ============================================================
    // 文件创建方法
    // ============================================================

    /// <summary>
    /// 创建模拟回收站文件（$R{random}.{ext} 格式）
    /// 与 Windows 回收站的实际文件结构一致
    /// </summary>
    /// <param name="originalPath">原始文件路径（用于模拟还原目标路径）</param>
    /// <param name="contentSize">文件内容大小（字节），默认 1KB</param>
    /// <returns>(回收站路径, SHA-256哈希值)</returns>
    public (string RecycleBinPath, string Sha256Hash) CreateRecycleBinFile(
        string originalPath,
        long contentSize = 1024)
    {
        // 生成 Windows 回收站风格的文件名: $R + 6位随机十六进制 + 原始扩展名
        string randomHex = Random.Shared.Next(0x100000, 0xFFFFFF).ToString("X6");
        string originalExt = Path.GetExtension(originalPath);
        string recycleFileName = $"$R{randomHex}{originalExt}";
        string recyclePath = Path.Combine(_recycleBinDir, recycleFileName);

        // 生成随机内容并写入
        byte[] content = new byte[contentSize];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(recyclePath, content);

        // 计算 SHA-256
        string sha256Hash = ComputeSha256Hex(content);

        _createdFiles.Add(recyclePath);
        return (recyclePath, sha256Hash);
    }

    /// <summary>
    /// 批量创建回收站文件
    /// </summary>
    /// <param name="count">文件数量</param>
    /// <param name="originalPaths">原始文件路径列表（可选的还原目标路径）</param>
    /// <returns>创建的回收站文件信息列表</returns>
    public List<(string RecycleBinPath, string Sha256Hash)> CreateBatch(
        int count,
        string[]? originalPaths = null)
    {
        var results = new List<(string, string)>(count);

        for (int i = 0; i < count; i++)
        {
            string originalPath = originalPaths != null && i < originalPaths.Length
                ? originalPaths[i]
                : $@"C:\DeletedFiles\deleted_file_{i}.tmp";

            long size = Random.Shared.NextInt64(1024, 100 * 1024 * 1024); // 1KB ~ 100MB
            var result = CreateRecycleBinFile(originalPath, size);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// 创建具有特定 SHA-256 哈希值的回收站文件
    /// 用于测试回滚管理器按哈希匹配文件的场景
    /// </summary>
    public (string RecycleBinPath, string Sha256Hash) CreateFileWithContent(byte[] content)
    {
        string randomHex = Random.Shared.Next(0x100000, 0xFFFFFF).ToString("X6");
        string recyclePath = Path.Combine(_recycleBinDir, $"$R{randomHex}.tmp");

        File.WriteAllBytes(recyclePath, content);
        string sha256Hash = ComputeSha256Hex(content);

        _createdFiles.Add(recyclePath);
        return (recyclePath, sha256Hash);
    }

    // ============================================================
    // 查询方法
    // ============================================================

    /// <summary>获取回收站中的文件计数</summary>
    public int GetFileCount() => _createdFiles.Count;

    /// <summary>检查指定文件是否存在于回收站中</summary>
    public bool FileExists(string recycleBinPath) => File.Exists(recycleBinPath);

    /// <summary>列出回收站中所有文件</summary>
    public IReadOnlyList<string> ListFiles() => _createdFiles.AsReadOnly();

    // ============================================================
    // 工具方法
    // ============================================================

    /// <summary>获取当前用户的 SID</summary>
    public static string GetCurrentUserSid()
    {
        return WindowsIdentity.GetCurrent().User?.Value
               ?? "S-1-5-18"; // 回退到 SYSTEM SID
    }

    /// <summary>计算字节数组的 SHA-256 哈希（小写十六进制）</summary>
    public static string ComputeSha256Hex(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    // ============================================================
    // 清理
    // ============================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var file in _createdFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { /* 清理失败不抛异常，文件在 temp 目录下次由系统清理 */ }
        }
    }
}
