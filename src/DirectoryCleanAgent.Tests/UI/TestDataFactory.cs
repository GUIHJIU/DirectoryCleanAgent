using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Tests.UI;

/// <summary>
/// 测试数据工厂 — 为 C2 单元测试提供可复用的 FileDecisionCache 和 FileItem 测试数据。
/// 所有数据均为确定性生成（固定种子），确保测试结果可重复。
/// </summary>
public static class TestDataFactory
{
    /// <summary>
    /// 混合 FinalAction 场景的预设缓存数据。
    /// 3 AutoDelete + 2 SuggestDelete + 1 ManualReview + 2 Protected = 8 条
    /// 用于验证分组视图仅展示 6 条（排除 2 条 Protected）。
    /// </summary>
    public static readonly List<FileDecisionCache> MixedActionCache = new()
    {
        CreateCache(@"\\?\C:\Windows\Temp\temp1.tmp", "SystemTempFiles", FinalAction.AutoDelete, 50_000_000, -1),
        CreateCache(@"\\?\C:\Windows\Temp\temp2.tmp", "SystemTempFiles", FinalAction.AutoDelete, 30_000_000, -1),
        CreateCache(@"\\?\C:\Windows\Temp\temp3.tmp", "SystemTempFiles", FinalAction.AutoDelete, 20_000_000, -2),
        CreateCache(@"\\?\C:\Users\Admin\AppData\Local\Chrome\Cache\cache1.dat", "AppCache", FinalAction.SuggestDelete, 200_000_000, -5),
        CreateCache(@"\\?\C:\Users\Admin\AppData\Local\Chrome\Cache\cache2.dat", "AppCache", FinalAction.SuggestDelete, 150_000_000, -7),
        CreateCache(@"\\?\C:\Program Files\App\logs\crash.dmp", "LogsAndDumps", FinalAction.ManualReview, 2_000_000_000, -15),
        CreateCache(@"\\?\C:\Windows\System32\ntdll.dll", "Protected", FinalAction.Protected, 2_048_000, -999),
        CreateCache(@"\\?\C:\Windows\System32\kernel32.dll", "Protected", FinalAction.Protected, 1_024_000, -999),
    };

    /// <summary>
    /// 路径分组测试数据：2个顶层目录各有 3 条文件
    /// </summary>
    public static readonly List<FileDecisionCache> PathGroupedCache = new()
    {
        CreateCache(@"\\?\C:\Windows\Temp\file1.tmp", "SystemTempFiles", FinalAction.AutoDelete, 10_000_000, -1),
        CreateCache(@"\\?\C:\Windows\System32\file2.dll", "SystemTempFiles", FinalAction.SuggestDelete, 20_000_000, -2),
        CreateCache(@"\\?\C:\Windows\Logs\file3.log", "LogsAndDumps", FinalAction.ManualReview, 30_000_000, -3),
        CreateCache(@"\\?\C:\Users\Admin\file4.txt", "Uncategorized", FinalAction.SuggestDelete, 40_000_000, -4),
        CreateCache(@"\\?\C:\Users\Admin\file5.pdf", "OldDownloads", FinalAction.AutoDelete, 50_000_000, -5),
        CreateCache(@"\\?\C:\Users\Admin\file6.zip", "OldDownloads", FinalAction.SuggestDelete, 60_000_000, -6),
    };

    /// <summary>
    /// 时间分组测试数据：覆盖 5 个时间桶
    /// </summary>
    public static List<FileDecisionCache> TimeGroupedCache => new()
    {
        CreateCache(@"\\?\C:\tmp\f1.tmp", "SystemTempFiles", FinalAction.AutoDelete, 1_000_000, 0),      // 今天
        CreateCache(@"\\?\C:\tmp\f2.tmp", "SystemTempFiles", FinalAction.AutoDelete, 2_000_000, -3),     // 最近7天
        CreateCache(@"\\?\C:\tmp\f3.tmp", "SystemTempFiles", FinalAction.SuggestDelete, 3_000_000, -15),  // 最近30天
        CreateCache(@"\\?\C:\tmp\f4.tmp", "SystemTempFiles", FinalAction.SuggestDelete, 4_000_000, -60),  // 30-90天
        CreateCache(@"\\?\C:\tmp\f5.tmp", "SystemTempFiles", FinalAction.ManualReview, 5_000_000, -200),  // 90天以上
    };

    /// <summary>
    /// 大小分组测试数据：覆盖 5 个大小桶
    /// </summary>
    public static readonly List<FileDecisionCache> SizeGroupedCache = new()
    {
        CreateCache(@"\\?\C:\tmp\s1.tmp", "SystemTempFiles", FinalAction.AutoDelete, 500_000, -1),           // <1MB
        CreateCache(@"\\?\C:\tmp\s2.tmp", "SystemTempFiles", FinalAction.AutoDelete, 5_000_000, -1),         // 1-10MB
        CreateCache(@"\\?\C:\tmp\s3.tmp", "SystemTempFiles", FinalAction.SuggestDelete, 50_000_000, -1),      // 10-100MB
        CreateCache(@"\\?\C:\tmp\s4.tmp", "SystemTempFiles", FinalAction.SuggestDelete, 500_000_000, -1),     // 100MB-1GB
        CreateCache(@"\\?\C:\tmp\s5.tmp", "SystemTempFiles", FinalAction.ManualReview, 2_000_000_000, -1),    // >1GB
    };

    /// <summary>
    /// 空缓存（测试空状态）
    /// </summary>
    public static readonly List<FileDecisionCache> EmptyCache = new();

    /// <summary>
    /// 生成指定数量的通用 FileDecisionCache 条目
    /// </summary>
    public static List<FileDecisionCache> CreateCacheEntries(int count)
    {
        var entries = new List<FileDecisionCache>();
        for (int i = 0; i < count; i++)
        {
            entries.Add(CreateCache(
                $@"\\?\C:\tmp\file_{i:D5}.tmp",
                "SystemTempFiles",
                FinalAction.AutoDelete,
                1_000_000 + i * 1000,
                -i));
        }
        return entries;
    }

    /// <summary>
    /// 生成指定数量的 FileItem 条目
    /// </summary>
    public static List<FileItem> CreateFileItems(int count)
    {
        var files = new List<FileItem>();
        for (int i = 0; i < count; i++)
        {
            files.Add(new FileItem
            {
                FilePath = $@"\\?\C:\tmp\file_{i:D5}.tmp",
                SizeBytes = 1_000_000 + i * 1000,
                LastWriteTime = DateTime.UtcNow.AddDays(-i),
                Extension = ".tmp",
                EverythingSortKey = $"{i:D8}"
            });
        }
        return files;
    }

    /// <summary>
    /// 创建单条 FileDecisionCache 条目
    /// </summary>
    private static FileDecisionCache CreateCache(
        string path,
        string semanticCategory,
        FinalAction action,
        long sizeBytes,
        int daysAgo)
    {
        return new FileDecisionCache
        {
            FilePath = path,
            SizeBytes = sizeBytes,
            LastWriteTime = DateTime.UtcNow.AddDays(daysAgo),
            RuleVerdict = action == FinalAction.Protected ? RuleVerdict.Keep : RuleVerdict.SuggestDelete,
            SemanticCategory = semanticCategory,
            FinalAction = action,
            CacheVersion = 1
        };
    }
}
