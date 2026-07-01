// ============================================================
// Program.cs
// 功能: TestVolumeGenerator CLI 入口
// 用于生成含大量文件的 NTFS 测试卷
//
// 使用方式:
//   dotnet run -- --root D:\TestVolume --file-count 2000000 --profile realistic
//   dotnet run -- --root D:\TestVolume --file-count 100000 --profile quick
//   dotnet run -- --root D:\TestVolume --file-count 500000 --profile rule
// ============================================================

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using DirectoryCleanAgent.Tools.TestVolumeGenerator;

// ---- CLI 参数定义 ----

var rootOption = new Option<string>(
    name: "--root",
    description: "测试卷根目录路径（必须是 NTFS 卷）")
{
    IsRequired = true,
};

var fileCountOption = new Option<int>(
    name: "--file-count",
    description: "目标生成文件总数",
    getDefaultValue: () => 2000000);

var profileOption = new Option<string>(
    name: "--profile",
    description: "文件分布策略: realistic（真实分布）, rule（规则引擎测试）, quick（快速冒烟）",
    getDefaultValue: () => "realistic");

var seedOption = new Option<int>(
    name: "--seed",
    description: "随机种子（相同种子产生相同文件分布，用于可复现测试）",
    getDefaultValue: () => 42);

var skipExistingOption = new Option<bool>(
    name: "--skip-existing",
    description: "跳过已存在的文件（用于断点续传）",
    getDefaultValue: () => false);

var parallelismOption = new Option<int>(
    name: "--parallelism",
    description: "并行度（0=自动，即 CPU 核心数）",
    getDefaultValue: () => 0);

var rootCommand = new RootCommand("智能磁盘清理工具 — 测试卷生成器 (D1.5)")
{
    rootOption,
    fileCountOption,
    profileOption,
    seedOption,
    skipExistingOption,
    parallelismOption,
};

// ---- 主入口 ----

rootCommand.SetHandler(async (InvocationContext ctx) =>
{
    var rootPath = ctx.ParseResult.GetValueForOption(rootOption)!;
    var fileCount = ctx.ParseResult.GetValueForOption(fileCountOption);
    var profileName = ctx.ParseResult.GetValueForOption(profileOption)!;
    var seed = ctx.ParseResult.GetValueForOption(seedOption);
    var skipExisting = ctx.ParseResult.GetValueForOption(skipExistingOption);
    var parallelism = ctx.ParseResult.GetValueForOption(parallelismOption);

    try
    {
        await GenerateAsync(rootPath, fileCount, profileName, seed, skipExisting, parallelism);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine();
        Console.WriteLine("⚠ 操作已取消");
        Environment.ExitCode = 3;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"致命错误: {ex.Message}");
        Console.Error.WriteLine($"堆栈: {ex.StackTrace}");
        Environment.ExitCode = 1;
    }
});

return await rootCommand.InvokeAsync(args);

// ---- 核心生成逻辑 ----

static async Task<int> GenerateAsync(
    string rootPath,
    int fileCount,
    string profileName,
    int seed,
    bool skipExisting,
    int parallelism)
{
    // ---- 步骤 0: 环境预检 ----

    Console.WriteLine("============================================================");
    Console.WriteLine("  智能磁盘清理工具 — 测试卷生成器 (D1.5)");
    Console.WriteLine("============================================================");
    Console.WriteLine($"  目标路径: {rootPath}");
    Console.WriteLine($"  文件数量: {fileCount:N0}");
    Console.WriteLine($"  分布策略: {profileName}");
    Console.WriteLine($"  随机种子: {seed}");
    Console.WriteLine($"  并行度: {(parallelism > 0 ? parallelism : Environment.ProcessorCount)}");
    Console.WriteLine($"  断点续传: {(skipExisting ? "是" : "否")}");
    Console.WriteLine("============================================================");
    Console.WriteLine();

    // 检查目标盘是否为 NTFS
    try
    {
        var rootDir = Directory.CreateDirectory(rootPath);
        var driveInfo = new DriveInfo(rootDir.Root.FullName);

        if (driveInfo.DriveFormat != "NTFS")
        {
            Console.WriteLine($"⚠ 警告: 目标磁盘格式为 {driveInfo.DriveFormat}，非 NTFS。");
            Console.WriteLine("  Everything SDK 仅索引 NTFS 卷，但文件生成可以继续。");
            Console.WriteLine("  建议使用 NTFS 格式化的卷作为测试目标。");
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine($"✓ 目标磁盘格式: NTFS (正常)");
        }

        Console.WriteLine($"  可用空间: {driveInfo.AvailableFreeSpace / (1024.0 * 1024 * 1024):F1} GB");
        Console.WriteLine($"  总大小: {driveInfo.TotalSize / (1024.0 * 1024 * 1024):F1} GB");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"无法获取磁盘信息: {ex.Message}");
    }

    // ---- 步骤 1: 获取分布配置 ----

    FileDistributionProfile profile = profileName.ToLowerInvariant() switch
    {
        "realistic" => FileDistributionProfile.Realistic,
        "rule" => FileDistributionProfile.RuleEngine,
        "quick" => FileDistributionProfile.Quick,
        _ => throw new ArgumentException($"未知的分布策略: {profileName}。可选: realistic, rule, quick"),
    };

    Console.WriteLine($"使用分布策略: {profile.Name}");
    Console.WriteLine($"  目录节点数: {profile.DirectoryStructure.Length}");
    Console.WriteLine($"  大小分布桶: {profile.SizeDistribution.Length}");
    Console.WriteLine($"  年龄分布桶: {profile.AgeDistribution.Length}");
    Console.WriteLine();

    // ---- 步骤 2: 创建文件写入器并执行 ----

    var progress = new Progress<GenerationProgress>(p =>
    {
        // 每秒报告一次进度（避免控制台 I/O 成为瓶颈）
        Console.Write(
            $"\r进度: {p.CompletedFiles:N0} / {p.TotalFiles:N0} " +
            $"({(double)p.CompletedFiles / p.TotalFiles * 100:F1}%) | " +
            $"速度: {p.FilesPerSecond:F1} 文件/秒 | " +
            $"错误: {p.ErrorFiles} | " +
            $"预计剩余: {p.EstimatedRemaining:hh\\:mm\\:ss}   ");
    });

    var writer = new ParallelFileWriter(
        rootPath: rootPath,
        profile: profile,
        seed: seed,
        skipExisting: skipExisting,
        maxDegreeOfParallelism: parallelism,
        progress: progress);

    // 支持 Ctrl+C 取消
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        Console.WriteLine();
        Console.WriteLine("正在取消... (请等待当前批次完成)");
        cts.Cancel();
        e.Cancel = true;
    };

    var result = writer.Generate(fileCount, cts.Token);

    // ---- 步骤 3: 输出结果 ----

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine("============================================================");
    Console.WriteLine("  生成完成");
    Console.WriteLine("============================================================");
    Console.WriteLine($"  成功生成: {result.CompletedFiles:N0} 个文件");
    Console.WriteLine($"  错误数量: {result.ErrorFiles:N0}");
    Console.WriteLine($"  总耗时: {result.ElapsedSeconds:F1} 秒 ({result.ElapsedSeconds / 60:F1} 分钟)");
    Console.WriteLine($"  平均速度: {result.FilesPerSecond:F1} 文件/秒");
    Console.WriteLine("============================================================");

    // 检查是否有文件生成（验证基本功能）
    if (result.CompletedFiles == 0)
    {
        Console.Error.WriteLine("错误: 未生成任何文件，请检查目标路径权限和日志输出");
        return 1;
    }

    return result.ErrorFiles > 0 ? 2 : 0;
}
