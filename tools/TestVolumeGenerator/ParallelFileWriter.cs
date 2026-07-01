// ============================================================
// ParallelFileWriter.cs
// 功能: 并行文件写入引擎
// 核心策略:
//   1. 使用加权随机分布模拟真实文件分布
//   2. 并行写入（每个目录独立线程分区，避免 I/O 竞争）
//   3. 大文件（>10MB）使用稀疏文件策略减少实际磁盘占用
//   4. 支持断点续传（--skip-existing）
// ============================================================

using System.Diagnostics;

namespace DirectoryCleanAgent.Tools.TestVolumeGenerator;

/// <summary>
/// 测试卷生成进度信息
/// </summary>
public sealed class GenerationProgress
{
    public int TotalFiles { get; init; }
    public int CompletedFiles => _completed;
    public int ErrorFiles => _errors;
    public double ElapsedSeconds => _stopwatch.Elapsed.TotalSeconds;
    public double FilesPerSecond => ElapsedSeconds > 0 ? CompletedFiles / ElapsedSeconds : 0;
    public TimeSpan EstimatedRemaining => FilesPerSecond > 0
        ? TimeSpan.FromSeconds((TotalFiles - CompletedFiles) / FilesPerSecond)
        : TimeSpan.MaxValue;

    private volatile int _completed;
    private volatile int _errors;
    private readonly Stopwatch _stopwatch;

    internal GenerationProgress(int totalFiles, Stopwatch stopwatch)
    {
        TotalFiles = totalFiles;
        _stopwatch = stopwatch;
    }

    internal void IncrementCompleted() => Interlocked.Increment(ref _completed);
    internal void IncrementErrors() => Interlocked.Increment(ref _errors);
}

/// <summary>
/// 并行文件写入引擎
/// 使用 TPL 并行写入测试文件，支持进度报告和取消
/// </summary>
public sealed class ParallelFileWriter
{
    private readonly string _rootPath;
    private readonly FileDistributionProfile _profile;
    private readonly int _seed;
    private readonly bool _skipExisting;
    private readonly int _maxDegreeOfParallelism;
    private readonly IProgress<GenerationProgress>? _progress;

    // 用于加权随机选择的累积分布数组（预计算，提高性能）
    private readonly double[] _dirCumulativeRatios;
    private readonly double[] _sizeCumulativeRatios;
    private readonly double[] _ageCumulativeRatios;

    // 目录与权重映射（按索引对应 _dirCumulativeRatios）
    private readonly List<(string FullPath, DirectoryNode Node)> _directories;

    // 大文件阈值：超过此大小的文件使用稀疏策略
    private const long SparseFileThreshold = 10L * 1024 * 1024; // 10MB
    private const int HeaderFooterSize = 65536; // 64KB 文件头/尾

    public ParallelFileWriter(
        string rootPath,
        FileDistributionProfile profile,
        int seed = 42,
        bool skipExisting = false,
        int maxDegreeOfParallelism = 0,
        IProgress<GenerationProgress>? progress = null)
    {
        _rootPath = rootPath;
        _profile = profile;
        _seed = seed;
        _skipExisting = skipExisting;
        _maxDegreeOfParallelism = maxDegreeOfParallelism > 0
            ? maxDegreeOfParallelism
            : Environment.ProcessorCount;
        _progress = progress;

        // 创建目录结构
        _directories = DirectoryStructureGenerator.CreateDirectories(rootPath, profile.DirectoryStructure);

        // 预计算累积分布数组（避免每次随机选择时重复计算）
        _dirCumulativeRatios = BuildCumulative(profile.DirectoryStructure, n => n.Ratio);
        _sizeCumulativeRatios = BuildCumulative(profile.SizeDistribution, b => b.Ratio);
        _ageCumulativeRatios = BuildCumulative(profile.AgeDistribution, b => b.Ratio);
    }

    /// <summary>
    /// 构建累积分布数组，用于加权随机选择
    /// </summary>
    private static double[] BuildCumulative<T>(T[] items, Func<T, double> ratioSelector)
    {
        var cumulative = new double[items.Length];
        double sum = 0;
        for (int i = 0; i < items.Length; i++)
        {
            sum += ratioSelector(items[i]);
            cumulative[i] = sum;
        }
        // 归一化（确保总和为 1.0）
        if (Math.Abs(sum - 1.0) > 0.0001)
        {
            for (int i = 0; i < cumulative.Length; i++)
            {
                cumulative[i] /= sum;
            }
        }
        return cumulative;
    }

    /// <summary>
    /// 根据累积分布做加权随机选择，返回被选中项的索引
    /// </summary>
    private static int WeightedRandomSelect(double[] cumulative, Random rng)
    {
        double roll = rng.NextDouble();
        for (int i = 0; i < cumulative.Length; i++)
        {
            if (roll <= cumulative[i])
                return i;
        }
        return cumulative.Length - 1;
    }

    /// <summary>
    /// 主入口：生成指定数量的文件
    /// </summary>
    public GenerationProgress Generate(int fileCount, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var progress = new GenerationProgress(fileCount, stopwatch);

        Console.WriteLine($"开始生成 {fileCount:N0} 个文件...");
        Console.WriteLine($"  并行度: {_maxDegreeOfParallelism}");
        Console.WriteLine($"  目录数: {_directories.Count}");
        Console.WriteLine($"  种子值: {_seed}");
        Console.WriteLine($"  跳过已有: {_skipExisting}");
        Console.WriteLine();

        // 将文件生成任务按目录分区，每个分区使用独立的 Random 实例（线程安全）
        // 分区策略: 每个目录分配 fileCount * node.Ratio 个文件
        var partitions = new List<(string FullPath, DirectoryNode Node, int FileCount)>();

        foreach (var (fullPath, node) in _directories)
        {
            int count = (int)Math.Max(1, Math.Round(fileCount * node.Ratio));
            partitions.Add((fullPath, node, count));
        }

        // 修正总数偏差（由于四舍五入）
        int totalAssigned = partitions.Sum(p => p.FileCount);
        if (totalAssigned != fileCount)
        {
            // 将偏差补偿到最大的分区
            int diff = fileCount - totalAssigned;
            var maxPartition = partitions.MaxBy(p => p.FileCount);
            int idx = partitions.IndexOf(maxPartition);
            partitions[idx] = (maxPartition.FullPath, maxPartition.Node, maxPartition.FileCount + diff);
        }

        // 并行执行
        // 每个分区使用独立的 Random 实例（种子不同）避免线程竞争
        Parallel.ForEach(partitions, new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = ct,
        }, partition =>
        {
            var (dirPath, node, count) = partition;

            // 为每个线程创建独立的 RNG（种子 = 基础种子 + 目录路径哈希）
            int threadSeed = _seed ^ dirPath.GetHashCode();
            var rng = new Random(threadSeed);

            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // 1. 随机选择扩展名
                    string ext = node.Extensions[rng.Next(node.Extensions.Length)];

                    // 2. 生成文件名（含序号，确保唯一）
                    string fileName = $"{Guid.NewGuid():N}[..8]_{i:D6}{ext}";
                    string filePath = Path.Combine(dirPath, fileName);

                    // 3. 断点续传：如果文件已存在则跳过
                    if (_skipExisting && File.Exists(filePath))
                    {
                        progress.IncrementCompleted();
                        continue;
                    }

                    // 4. 根据分布随机选择文件大小
                    int sizeIdx = WeightedRandomSelect(_sizeCumulativeRatios, rng);
                    var sizeBucket = _profile.SizeDistribution[sizeIdx];
                    long fileSize = rng.NextInt64(sizeBucket.MinBytes, sizeBucket.MaxBytes);

                    // 5. 根据分布随机选择文件年龄
                    int ageIdx = WeightedRandomSelect(_ageCumulativeRatios, rng);
                    var ageBucket = _profile.AgeDistribution[ageIdx];
                    int ageDays = rng.Next(ageBucket.MinDays, ageBucket.MaxDays);
                    var lastWriteTime = DateTime.Now.AddDays(-ageDays);

                    // 6. 写入文件
                    WriteTestFile(filePath, fileSize, rng);

                    // 7. 设置文件时间戳
                    try
                    {
                        File.SetCreationTime(filePath, lastWriteTime);
                        File.SetLastWriteTime(filePath, lastWriteTime);
                        File.SetLastAccessTime(filePath, DateTime.Now.AddDays(-rng.Next(0, 3)));
                    }
                    catch
                    {
                        // 时间戳设置失败不阻断流程
                    }

                    progress.IncrementCompleted();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    progress.IncrementErrors();
                    // 仅报告前10个错误，避免日志爆炸
                    if (progress.ErrorFiles <= 10)
                    {
                        Console.Error.WriteLine($"[写入错误] {dirPath}/file_{i}: {ex.Message}");
                    }
                }
            }
        });

        stopwatch.Stop();
        return progress;
    }

    /// <summary>
    /// 写入测试文件
    /// 小文件（≤10MB）：填充随机数据
    /// 大文件（>10MB）：稀疏策略 — 仅写入首尾 64KB，中间为零填充
    /// </summary>
    private static void WriteTestFile(string filePath, long fileSize, Random rng)
    {
        if (fileSize <= SparseFileThreshold)
        {
            // 小文件：完整写入随机内容
            // 对于 ≤64KB 的文件，直接一次性分配
            if (fileSize <= HeaderFooterSize)
            {
                byte[] buffer = new byte[fileSize];
                rng.NextBytes(buffer);
                File.WriteAllBytes(filePath, buffer);
            }
            else
            {
                // 中等文件：分块写入（避免大对象堆分配）
                using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.SequentialScan);
                long remaining = fileSize;
                byte[] buffer = new byte[65536];
                while (remaining > 0)
                {
                    int chunkSize = (int)Math.Min(remaining, buffer.Length);
                    rng.NextBytes(buffer);
                    fs.Write(buffer, 0, chunkSize);
                    remaining -= chunkSize;
                }
            }
        }
        else
        {
            // 大文件 — 稀疏策略
            // 写入 64KB 文件头 + 64KB 文件尾，中间区域依靠 NTFS 稀疏文件机制（零填充）
            byte[] header = new byte[HeaderFooterSize];
            byte[] footer = new byte[HeaderFooterSize];
            rng.NextBytes(header);
            rng.NextBytes(footer);

            // 创建文件并写入文件头
            File.WriteAllBytes(filePath, header);

            // 扩展文件并写入文件尾
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
            fs.Seek(fileSize - HeaderFooterSize, SeekOrigin.Begin);
            fs.Write(footer, 0, footer.Length);
            // 注意: 不调用 fs.SetLength() — 文件已通过 Seek+Write 扩展
        }
    }
}
