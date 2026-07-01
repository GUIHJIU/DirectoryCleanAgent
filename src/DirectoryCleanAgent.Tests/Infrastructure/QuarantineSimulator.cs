// ============================================================
// QuarantineSimulator.cs
// 功能: 隔离区目录/文件模拟器
// 在集成测试中创建符合隔离区命名规范的测试文件
// 遵循 B6 隔离区管理器定义的命名格式:
//   {SHA256前16位}_{原始文件名}_{Unix时间戳秒}.quarantine
// ============================================================

using System.Security.Cryptography;
using System.Text;

namespace DirectoryCleanAgent.Tests.Infrastructure;

/// <summary>
/// 隔离区模拟器 — 创建和管理符合隔离区命名规范的测试文件
/// 用于集成测试中模拟隔离区场景（恢复、过期清理、容量检测等）
/// </summary>
public sealed class QuarantineSimulator : IDisposable
{
    private readonly string _quarantineDir;
    private readonly List<string> _createdFiles = new();
    private bool _disposed;

    /// <summary>隔离区目录路径</summary>
    public string QuarantineDirectory => _quarantineDir;

    /// <summary>SHA-256 前缀长度（与 QuarantineManager.Sha256PrefixLength 一致）</summary>
    public const int Sha256PrefixLength = 16;

    /// <summary>隔离区文件扩展名</summary>
    public const string QuarantineExtension = ".quarantine";

    /// <summary>
    /// 构造隔离区模拟器
    /// </summary>
    /// <param name="basePath">隔离区父目录（如 %LocalAppData%\SpaceClear）</param>
    public QuarantineSimulator(string basePath)
    {
        _quarantineDir = Path.Combine(basePath, "quarantine");
        if (!Directory.Exists(_quarantineDir))
        {
            Directory.CreateDirectory(_quarantineDir);
        }
    }

    // ============================================================
    // 文件创建方法
    // ============================================================

    /// <summary>
    /// 创建一个隔离区文件
    /// </summary>
    /// <param name="originalFileName">原始文件名（如 "文档.docx"）</param>
    /// <param name="contentSize">文件内容大小（字节），默认 1KB</param>
    /// <param name="deletedAt">删除时间，默认当前时间</param>
    /// <returns>(隔离区完整路径, SHA-256哈希值, 原始文件名)</returns>
    public (string QuarantinePath, string Sha256Hash, string OriginalFileName) CreateQuarantineFile(
        string originalFileName,
        long contentSize = 1024,
        DateTimeOffset? deletedAt = null)
    {
        var deleteTime = deletedAt ?? DateTimeOffset.UtcNow;
        long unixSeconds = deleteTime.ToUnixTimeSeconds();

        // 生成随机文件内容并计算 SHA-256
        byte[] content = new byte[contentSize];
        Random.Shared.NextBytes(content);
        string fullSha256 = ComputeSha256Hex(content);

        // 提取 SHA-256 前 16 位作为前缀
        string sha256Prefix = fullSha256[..Math.Min(Sha256PrefixLength, fullSha256.Length)];

        // 按隔离区命名规范构造文件名: {SHA256前16位}_{原始文件名}_{Unix秒}.quarantine
        string quarantineFileName = $"{sha256Prefix}_{originalFileName}_{unixSeconds}{QuarantineExtension}";
        string quarantinePath = Path.Combine(_quarantineDir, quarantineFileName);

        // 写入文件
        File.WriteAllBytes(quarantinePath, content);
        _createdFiles.Add(quarantinePath);

        return (quarantinePath, fullSha256, originalFileName);
    }

    /// <summary>
    /// 批量创建隔离区文件
    /// </summary>
    /// <param name="count">文件数量</param>
    /// <returns>创建的隔离区文件信息列表</returns>
    public List<(string QuarantinePath, string Sha256Hash, string OriginalFileName)> CreateBatch(int count)
    {
        var results = new List<(string, string, string)>(count);
        string[] sampleNames = { "报告.docx", "数据.xlsx", "演示.pptx", "图片.png", "日志.log",
                                  "备份.zip", "安装包.msi", "代码.cs", "配置.json", "存档.tar.gz" };

        for (int i = 0; i < count; i++)
        {
            string name = sampleNames[i % sampleNames.Length];
            // 添加序号避免重名
            string uniqueName = Path.GetFileNameWithoutExtension(name) + $"_{i}" + Path.GetExtension(name);
            long size = Random.Shared.NextInt64(1024, 10 * 1024 * 1024); // 1KB ~ 10MB
            var result = CreateQuarantineFile(uniqueName, size,
                DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(0, 60)));
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// 创建过期隔离区文件（超过保留期限）
    /// </summary>
    /// <param name="originalFileName">原始文件名</param>
    /// <param name="daysOld">距今多少天前被删除</param>
    public (string QuarantinePath, string Sha256Hash) CreateExpiredFile(
        string originalFileName,
        int daysOld = 31)
    {
        var deleteTime = DateTimeOffset.UtcNow.AddDays(-daysOld);
        var (path, hash, _) = CreateQuarantineFile(originalFileName, deletedAt: deleteTime);
        return (path, hash);
    }

    /// <summary>
    /// 创建命名格式错误的隔离区文件（用于测试解析失败的边界条件）
    /// </summary>
    public string CreateMalformedFile()
    {
        string path = Path.Combine(_quarantineDir, $"bad_format_file_{Guid.NewGuid():N}[..8]");
        File.WriteAllText(path, "malformed content");
        _createdFiles.Add(path);
        return path;
    }

    // ============================================================
    // 查询方法
    // ============================================================

    /// <summary>获取隔离区中的文件计数</summary>
    public int GetFileCount() => _createdFiles.Count;

    /// <summary>获取隔离区中所有文件的总大小</summary>
    public long GetTotalSize() => _createdFiles.Sum(f => new FileInfo(f).Length);

    /// <summary>检查指定文件是否存在于隔离区中</summary>
    public bool FileExists(string quarantinePath) => File.Exists(quarantinePath);

    // ============================================================
    // 工具方法
    // ============================================================

    /// <summary>计算字节数组的 SHA-256 哈希（小写十六进制）</summary>
    public static string ComputeSha256Hex(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>计算文件的 SHA-256 哈希</summary>
    public static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
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
            catch { /* 清理失败不抛异常 */ }
        }

        // 不删除隔离区目录本身（可能与其他测试共享）
    }
}
