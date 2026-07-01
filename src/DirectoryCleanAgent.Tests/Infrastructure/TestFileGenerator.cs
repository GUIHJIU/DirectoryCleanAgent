// ============================================================
// TestFileGenerator.cs
// 功能: 编程式测试文件生成器
// 在 C# 测试代码中生成小批量测试文件（100-100,000 个）
// 用于集成测试的 SetUp 阶段快速准备测试数据
// ============================================================

using System.Security.Cryptography;
using System.Text;

namespace DirectoryCleanAgent.Tests.Infrastructure;

/// <summary>
/// 文件分布快速配置
/// </summary>
public sealed class FileDistributionConfig
{
    /// <summary>目录路径（相对于根目录）</summary>
    public required string RelativePath { get; init; }

    /// <summary>扩展名列表</summary>
    public required string[] Extensions { get; init; }

    /// <summary>该目录下生成的文件数</summary>
    public required int FileCount { get; init; }

    /// <summary>最小文件大小（字节）</summary>
    public long MinSizeBytes { get; init; } = 1024;

    /// <summary>最大文件大小（字节）</summary>
    public long MaxSizeBytes { get; init; } = 1024 * 1024;

    /// <summary>最小文件年龄（天）</summary>
    public int MinAgeDays { get; init; } = 0;

    /// <summary>最大文件年龄（天）</summary>
    public int MaxAgeDays { get; init; } = 90;
}

/// <summary>
/// 测试文件生成器
/// 用于在集成测试中快速生成可控的测试文件集合
/// </summary>
public sealed class TestFileGenerator : IDisposable
{
    private readonly string _rootPath;
    private readonly List<string> _generatedFiles = new();
    private bool _disposed;

    public string RootPath => _rootPath;
    public IReadOnlyList<string> GeneratedFiles => _generatedFiles.AsReadOnly();
    public int FileCount => _generatedFiles.Count;

    /// <summary>
    /// 构造测试文件生成器
    /// </summary>
    /// <param name="rootPath">测试文件根目录</param>
    public TestFileGenerator(string rootPath)
    {
        _rootPath = rootPath;
        if (!Directory.Exists(_rootPath))
        {
            Directory.CreateDirectory(_rootPath);
        }
    }

    // ============================================================
    // 生成方法
    // ============================================================

    /// <summary>
    /// 根据分布配置生成测试文件
    /// </summary>
    /// <param name="configs">文件分布配置列表</param>
    /// <param name="progress">可选的进度报告</param>
    /// <returns>生成的文件完整路径列表</returns>
    public List<string> Generate(IReadOnlyList<FileDistributionConfig> configs, IProgress<int>? progress = null)
    {
        var results = new List<string>();

        foreach (var config in configs)
        {
            string dirPath = Path.Combine(_rootPath, config.RelativePath);
            if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
            }

            for (int i = 0; i < config.FileCount; i++)
            {
                string ext = config.Extensions[Random.Shared.Next(config.Extensions.Length)];
                string fileName = $"test_{i:D6}{ext}";
                string filePath = Path.Combine(dirPath, fileName);

                // 随机文件大小
                long size = Random.Shared.NextInt64(config.MinSizeBytes, config.MaxSizeBytes + 1);

                // 写入文件内容（使用稀疏策略：大文件仅填充首部）
                WriteFile(filePath, size);

                // 设置文件年龄
                int ageDays = Random.Shared.Next(config.MinAgeDays, config.MaxAgeDays + 1);
                try
                {
                    File.SetCreationTime(filePath, DateTime.Now.AddDays(-ageDays));
                    File.SetLastWriteTime(filePath, DateTime.Now.AddDays(-ageDays));
                }
                catch { /* 时间戳设置不阻断 */ }

                results.Add(filePath);
                _generatedFiles.Add(filePath);
                progress?.Report(i + 1);
            }
        }

        return results;
    }

    /// <summary>
    /// 生成指定数量的随机测试文件
    /// </summary>
    public List<string> GenerateRandom(int count, string subDir = "random")
    {
        return Generate(new[]
        {
            new FileDistributionConfig
            {
                RelativePath = subDir,
                Extensions = new[] { ".tmp", ".log", ".cache", ".dat", ".bak", ".dmp", ".zip", ".pdf" },
                FileCount = count,
                MinSizeBytes = 1024,
                MaxSizeBytes = 10 * 1024 * 1024,
                MinAgeDays = 0,
                MaxAgeDays = 365,
            },
        });
    }

    /// <summary>
    /// 生成触发规则引擎的测试文件集合
    /// 覆盖多种规则触发场景：临时文件、日志、缓存、下载旧文件、系统文件
    /// </summary>
    public List<string> GenerateForRuleEngine(int countPerCategory = 100)
    {
        return Generate(new[]
        {
            // 系统临时文件 → 触发硬规则 "系统临时文件"
            new FileDistributionConfig
            {
                RelativePath = @"Windows\Temp",
                Extensions = new[] { ".tmp", ".log", ".etl" },
                FileCount = countPerCategory,
                MinSizeBytes = 1024, MaxSizeBytes = 1024 * 1024,
                MinAgeDays = 7, MaxAgeDays = 90,
            },
            // 应用缓存 → 触发启发式规则 "应用缓存"
            new FileDistributionConfig
            {
                RelativePath = @"Users\TestUser\AppData\Local\Temp",
                Extensions = new[] { ".cache", ".dat", ".tmp" },
                FileCount = countPerCategory,
                MinSizeBytes = 1024, MaxSizeBytes = 10 * 1024 * 1024,
                MinAgeDays = 30, MaxAgeDays = 180,
            },
            // 下载旧文件 → 触发规则 "下载旧文件"
            new FileDistributionConfig
            {
                RelativePath = @"Users\TestUser\Downloads",
                Extensions = new[] { ".zip", ".msi", ".exe", ".pdf", ".iso" },
                FileCount = countPerCategory,
                MinSizeBytes = 1024 * 1024, MaxSizeBytes = 100 * 1024 * 1024,
                MinAgeDays = 90, MaxAgeDays = 365,
            },
            // 系统保护文件 → 触发硬规则 "系统关键文件"
            new FileDistributionConfig
            {
                RelativePath = @"Windows\System32",
                Extensions = new[] { ".dll", ".exe", ".sys" },
                FileCount = countPerCategory,
                MinSizeBytes = 1024, MaxSizeBytes = 10 * 1024 * 1024,
                MinAgeDays = 0, MaxAgeDays = 7,
            },
        });
    }

    // ============================================================
    // 文件哈希工具方法
    // ============================================================

    /// <summary>计算文件的 SHA-256 哈希值（小写十六进制）</summary>
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
    // 内部方法
    // ============================================================

    /// <summary>
    /// 写入测试文件
    /// 大文件（>10MB）使用稀疏策略减少实际磁盘 I/O
    /// </summary>
    private static void WriteFile(string filePath, long size)
    {
        const long sparseThreshold = 10L * 1024 * 1024; // 10MB
        const int headerSize = 65536; // 64KB

        if (size <= headerSize)
        {
            byte[] buffer = new byte[size];
            Random.Shared.NextBytes(buffer);
            File.WriteAllBytes(filePath, buffer);
        }
        else if (size <= sparseThreshold)
        {
            // 中等文件：分块写入
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write,
                FileShare.None, headerSize, FileOptions.SequentialScan);
            byte[] buffer = new byte[headerSize];
            long remaining = size;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, buffer.Length);
                Random.Shared.NextBytes(buffer);
                fs.Write(buffer, 0, chunk);
                remaining -= chunk;
            }
        }
        else
        {
            // 大文件 — 稀疏策略
            byte[] header = new byte[headerSize];
            Random.Shared.NextBytes(header);
            File.WriteAllBytes(filePath, header);

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
            byte[] footer = new byte[headerSize];
            Random.Shared.NextBytes(footer);
            fs.Seek(size - headerSize, SeekOrigin.Begin);
            fs.Write(footer, 0, footer.Length);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var file in _generatedFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { }
        }
    }
}
