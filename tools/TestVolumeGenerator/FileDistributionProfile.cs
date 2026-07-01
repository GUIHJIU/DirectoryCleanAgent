// ============================================================
// FileDistributionProfile.cs
// 功能: 定义测试文件分布配置（扩展名/大小/时间/目录结构）
// 用于生成具有真实分布特征的测试卷
// ============================================================

namespace DirectoryCleanAgent.Tools.TestVolumeGenerator;

/// <summary>
/// 单个目录节点的分布配置
/// </summary>
public sealed class DirectoryNode
{
    /// <summary>相对于卷根的路径，如 Windows\Temp</summary>
    public required string RelativePath { get; init; }

    /// <summary>该目录下的文件占总文件数的比例（0.0 ~ 1.0）</summary>
    public required double Ratio { get; init; }

    /// <summary>该目录下文件的扩展名集合</summary>
    public required string[] Extensions { get; init; }

    /// <summary>是否标记为受保护文件（如系统 DLL）</summary>
    public bool IsProtected { get; init; }
}

/// <summary>
/// 文件大小分布桶
/// </summary>
public sealed class SizeBucket
{
    /// <summary>最小字节数（含）</summary>
    public long MinBytes { get; init; }

    /// <summary>最大字节数（不含）</summary>
    public long MaxBytes { get; init; }

    /// <summary>该大小范围占总文件数的比例（0.0 ~ 1.0）</summary>
    public required double Ratio { get; init; }
}

/// <summary>
/// 文件年龄分布桶
/// </summary>
public sealed class AgeBucket
{
    /// <summary>最小天数（含）</summary>
    public int MinDays { get; init; }

    /// <summary>最大天数（不含）</summary>
    public int MaxDays { get; init; }

    /// <summary>该年龄范围占总文件数的比例（0.0 ~ 1.0）</summary>
    public required double Ratio { get; init; }
}

/// <summary>
/// 完整的文件分布配置
/// 定义测试卷中文件的目录分布、大小分布、年龄分布
/// </summary>
public sealed class FileDistributionProfile
{
    /// <summary>配置文件名称</summary>
    public required string Name { get; init; }

    /// <summary>目录结构树</summary>
    public required DirectoryNode[] DirectoryStructure { get; init; }

    /// <summary>文件大小分布</summary>
    public required SizeBucket[] SizeDistribution { get; init; }

    /// <summary>文件年龄分布</summary>
    public required AgeBucket[] AgeDistribution { get; init; }

    // ============================================================
    // 预定义配置策略
    // ============================================================

    /// <summary>
    /// 真实分布策略 — 模拟真实 Windows 磁盘的文件分布
    /// 用于性能测试（200万文件场景）
    /// </summary>
    public static FileDistributionProfile Realistic => new()
    {
        Name = "Realistic",
        DirectoryStructure = new[]
        {
            new DirectoryNode { RelativePath = @"Windows\System32",          Ratio = 0.08, Extensions = new[] { ".dll", ".exe", ".sys", ".mui", ".dat" }, IsProtected = true },
            new DirectoryNode { RelativePath = @"Windows\SysWOW64",          Ratio = 0.04, Extensions = new[] { ".dll", ".exe", ".mui" },           IsProtected = true },
            new DirectoryNode { RelativePath = @"Windows\Temp",              Ratio = 0.15, Extensions = new[] { ".tmp", ".log", ".etl", ".dmp" },   IsProtected = false },
            new DirectoryNode { RelativePath = @"Windows\Logs",              Ratio = 0.10, Extensions = new[] { ".log", ".etl", ".evtx" },          IsProtected = false },
            new DirectoryNode { RelativePath = @"Windows\Prefetch",          Ratio = 0.03, Extensions = new[] { ".pf" },                            IsProtected = false },
            new DirectoryNode { RelativePath = @"Program Files\AppCache",    Ratio = 0.06, Extensions = new[] { ".cache", ".dat", ".db", ".tmp" },  IsProtected = false },
            new DirectoryNode { RelativePath = @"Program Files (x86)\Cache", Ratio = 0.04, Extensions = new[] { ".cache", ".tmp", ".bak" },         IsProtected = false },
            new DirectoryNode { RelativePath = @"Users\Admin\AppData\Local\Temp",                    Ratio = 0.10, Extensions = new[] { ".tmp", ".log", ".cache", ".dmp" },    IsProtected = false },
            new DirectoryNode { RelativePath = @"Users\Admin\AppData\Local\Google\Chrome\Cache",     Ratio = 0.07, Extensions = new[] { ".dat", ".cache", ".tmp" },            IsProtected = false },
            new DirectoryNode { RelativePath = @"Users\Admin\AppData\Local\Microsoft\Edge\Cache",   Ratio = 0.05, Extensions = new[] { ".dat", ".cache" },                    IsProtected = false },
            new DirectoryNode { RelativePath = @"Users\Admin\AppData\Local\Microsoft\Windows\INetCache", Ratio = 0.03, Extensions = new[] { ".js", ".css", ".htm", ".png" }, IsProtected = false },
            new DirectoryNode { RelativePath = @"Users\Admin\Downloads",     Ratio = 0.10, Extensions = new[] { ".zip", ".msi", ".exe", ".pdf", ".iso", ".tar.gz" }, IsProtected = false },
            new DirectoryNode { RelativePath = @"Users\Admin\Documents",     Ratio = 0.05, Extensions = new[] { ".docx", ".xlsx", ".pptx", ".pdf" },              IsProtected = false },
            new DirectoryNode { RelativePath = @"ProgramData\PackageCache",  Ratio = 0.05, Extensions = new[] { ".cab", ".msi", ".msu", ".exe" },                IsProtected = false },
            new DirectoryNode { RelativePath = @"$Recycle.Bin\S-1-5-18",     Ratio = 0.05, Extensions = new[] { ".tmp", ".log", ".bak", ".old" },                IsProtected = false },
        },
        SizeDistribution = new[]
        {
            new SizeBucket { MinBytes = 1024,         MaxBytes = 10 * 1024,         Ratio = 0.20 },  // 1KB-10KB: 20%
            new SizeBucket { MinBytes = 10 * 1024,    MaxBytes = 100 * 1024,        Ratio = 0.25 },  // 10KB-100KB: 25%
            new SizeBucket { MinBytes = 100 * 1024,   MaxBytes = 1024 * 1024,       Ratio = 0.25 },  // 100KB-1MB: 25%
            new SizeBucket { MinBytes = 1024 * 1024,  MaxBytes = 10 * 1024 * 1024,  Ratio = 0.20 },  // 1MB-10MB: 20%
            new SizeBucket { MinBytes = 10 * 1024 * 1024, MaxBytes = 100 * 1024 * 1024, Ratio = 0.10 },  // 10MB-100MB: 10%
        },
        AgeDistribution = new[]
        {
            new AgeBucket { MinDays = 0,   MaxDays = 7,   Ratio = 0.25 },  // 最近7天: 25%
            new AgeBucket { MinDays = 7,   MaxDays = 30,  Ratio = 0.30 },  // 7-30天: 30%
            new AgeBucket { MinDays = 30,  MaxDays = 90,  Ratio = 0.22 },  // 30-90天: 22%
            new AgeBucket { MinDays = 90,  MaxDays = 365, Ratio = 0.18 },  // 90-365天: 18%
            new AgeBucket { MinDays = 365, MaxDays = 730, Ratio = 0.05 },  // 1-2年: 5%
        },
    };

    /// <summary>
    /// 规则引擎测试策略 — 文件类型分布确保触发各类规则
    /// </summary>
    public static FileDistributionProfile RuleEngine => new()
    {
        Name = "RuleEngine",
        DirectoryStructure = new[]
        {
            new DirectoryNode { RelativePath = @"Windows\Temp",   Ratio = 0.30, Extensions = new[] { ".tmp", ".log", ".etl", ".dmp" }, IsProtected = false },
            new DirectoryNode { RelativePath = @"Users\Downloads", Ratio = 0.30, Extensions = new[] { ".zip", ".msi", ".exe", ".iso" }, IsProtected = false },
            new DirectoryNode { RelativePath = @"Users\Temp",     Ratio = 0.25, Extensions = new[] { ".cache", ".bak", ".old" }, IsProtected = false },
            new DirectoryNode { RelativePath = @"Windows\System32", Ratio = 0.15, Extensions = new[] { ".dll", ".exe", ".sys" }, IsProtected = true },
        },
        SizeDistribution = new[]
        {
            new SizeBucket { MinBytes = 1024, MaxBytes = 100 * 1024 * 1024, Ratio = 1.0 },
        },
        AgeDistribution = new[]
        {
            new AgeBucket { MinDays = 30, MaxDays = 365, Ratio = 0.60 },
            new AgeBucket { MinDays = 0,  MaxDays = 30,  Ratio = 0.40 },
        },
    };

    /// <summary>
    /// 快速冒烟测试策略 — 最小文件集
    /// </summary>
    public static FileDistributionProfile Quick => new()
    {
        Name = "Quick",
        DirectoryStructure = new[]
        {
            new DirectoryNode { RelativePath = @"Windows\Temp", Ratio = 0.50, Extensions = new[] { ".tmp", ".log" }, IsProtected = false },
            new DirectoryNode { RelativePath = @"Users\Downloads", Ratio = 0.50, Extensions = new[] { ".zip", ".pdf" }, IsProtected = false },
        },
        SizeDistribution = new[]
        {
            new SizeBucket { MinBytes = 1024, MaxBytes = 1024 * 1024, Ratio = 1.0 },
        },
        AgeDistribution = new[]
        {
            new AgeBucket { MinDays = 30, MaxDays = 90, Ratio = 1.0 },
        },
    };
}
