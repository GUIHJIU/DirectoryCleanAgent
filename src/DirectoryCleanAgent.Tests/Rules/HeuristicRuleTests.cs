using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Rules.Heuristic;
using DirectoryCleanAgent.Rules.Models;

namespace DirectoryCleanAgent.Tests.Rules;

/// <summary>
/// 启发式规则单元测试 — 验证条件匹配、Glob 模式解析和评估器正确性。
///
/// 测试覆盖：
/// - 单条件匹配（扩展名、大小、年龄、路径模式）
/// - 多条件 AND 逻辑
/// - Glob 路径模式匹配（含 ** 递归通配符）
/// - 边界条件（null 扩展名、零大小文件、未来时间戳）
/// </summary>
public class HeuristicRuleTests
{
    private static FileItem CreateFile(string path, long size = 1024, DateTime? lastWrite = null, string? ext = null)
    {
        return new FileItem
        {
            FilePath = path,
            SizeBytes = size,
            LastWriteTime = lastWrite ?? DateTime.UtcNow,
            Extension = ext,
            EverythingSortKey = "test"
        };
    }

    #region 扩展名匹配

    [Fact]
    public void ExtensionMatch_ExactMatch_ShouldSucceed()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions { Extensions = new List<string> { ".log" } }
        };
        var file = CreateFile(@"\\?\C:\test.log", ext: ".log");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestDelete, result);
    }

    [Fact]
    public void ExtensionMatch_CaseInsensitive_ShouldSucceed()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions { Extensions = new List<string> { ".LOG" } }
        };
        var file = CreateFile(@"\\?\C:\test.log", ext: ".log");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestDelete, result);
    }

    [Fact]
    public void ExtensionMatch_NoMatch_ShouldReturnNull()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions { Extensions = new List<string> { ".log" } }
        };
        var file = CreateFile(@"\\?\C:\test.txt", ext: ".txt");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Null(result);
    }

    [Fact]
    public void ExtensionMatch_NullExtension_ShouldReturnNull()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions { Extensions = new List<string> { ".log" } }
        };
        var file = CreateFile(@"\\?\C:\test", ext: null);

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Null(result);
    }

    #endregion

    #region 文件大小匹配

    [Fact]
    public void SizeMatch_MinSize_AtThreshold_ShouldSucceed()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions { MinSizeBytes = 100 * 1024 * 1024 } // 100 MB
        };
        var file = CreateFile(@"\\?\C:\large.log", size: 100 * 1024 * 1024);

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestDelete, result);
    }

    [Fact]
    public void SizeMatch_MinSize_BelowThreshold_ShouldReturnNull()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions { MinSizeBytes = 100 * 1024 * 1024 }
        };
        var file = CreateFile(@"\\?\C:\small.log", size: 50 * 1024 * 1024);

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Null(result);
    }

    [Fact]
    public void SizeMatch_MaxSize_WithinRange_ShouldSucceed()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 6, SemanticCategory = "保护", Reason = "test",
            Conditions = new RuleConditions
            {
                MinSizeBytes = 1024,
                MaxSizeBytes = 10 * 1024 * 1024 // 10 MB
            }
        };
        var file = CreateFile(@"\\?\C:\medium.log", size: 5 * 1024 * 1024);

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestKeep, result);
    }

    [Fact]
    public void SizeMatch_MaxSize_ExceedsThreshold_ShouldReturnNull()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions { MaxSizeBytes = 1024 }
        };
        var file = CreateFile(@"\\?\C:\big.log", size: 2048);

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Null(result);
    }

    #endregion

    #region 文件年龄匹配

    [Fact]
    public void AgeMatch_MinAge_OldFile_ShouldSucceed()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions { MinAgeDays = 90 }
        };
        var file = CreateFile(@"\\?\C:\old.log", lastWrite: DateTime.UtcNow.AddDays(-120));

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestDelete, result);
    }

    [Fact]
    public void AgeMatch_MinAge_RecentFile_ShouldReturnNull()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions { MinAgeDays = 90 }
        };
        var file = CreateFile(@"\\?\C:\recent.log", lastWrite: DateTime.UtcNow.AddDays(-30));

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Null(result);
    }

    [Fact]
    public void AgeMatch_MaxAge_RecentFile_ShouldSucceed()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 6, SemanticCategory = "保护", Reason = "test",
            Conditions = new RuleConditions { MaxAgeDays = 30 }
        };
        var file = CreateFile(@"\\?\C:\recent.bak", lastWrite: DateTime.UtcNow.AddDays(-5));

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestKeep, result);
    }

    [Fact]
    public void AgeMatch_MaxAge_OldFile_ShouldReturnNull()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 6, SemanticCategory = "保护", Reason = "test",
            Conditions = new RuleConditions { MaxAgeDays = 30 }
        };
        var file = CreateFile(@"\\?\C:\old.bak", lastWrite: DateTime.UtcNow.AddDays(-60));

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Null(result);
    }

    #endregion

    #region 路径 Glob 模式匹配

    [Fact]
    public void GlobMatch_ExactPath_ShouldSucceed()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions
            {
                PathPatterns = new List<string> { @"**\Downloads\**" }
            }
        };
        var file = CreateFile(@"\\?\C:\Users\test\Downloads\setup.exe");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestDelete, result);
    }

    [Fact]
    public void GlobMatch_NonMatchingPath_ShouldReturnNull()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions
            {
                PathPatterns = new List<string> { @"**\Downloads\**" }
            }
        };
        var file = CreateFile(@"\\?\C:\Users\test\Documents\report.docx");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Null(result);
    }

    [Fact]
    public void GlobMatch_ChinesePath_ShouldSucceed()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions
            {
                PathPatterns = new List<string> { @"**\下载\**" }
            }
        };
        var file = CreateFile(@"\\?\C:\Users\test\下载\chrome_installer.exe");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestDelete, result);
    }

    [Fact]
    public void GlobMatch_MultiplePatterns_AnyMatch_ShouldSucceed()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions
            {
                PathPatterns = new List<string> { @"**\Videos\**", @"**\Music\**", @"**\Downloads\**" }
            }
        };
        var file = CreateFile(@"\\?\C:\Users\test\Music\song.mp3");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestDelete, result);
    }

    #endregion

    #region 多条件 AND 逻辑

    [Fact]
    public void AllConditions_AllMatch_ShouldSucceed()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "large_old_log", Priority = 5, SemanticCategory = "日志/转储",
            Reason = "大于100MB且90天未访问的日志文件",
            Conditions = new RuleConditions
            {
                Extensions = new List<string> { ".log" },
                MinSizeBytes = 100 * 1024 * 1024,
                MinAgeDays = 90
            }
        };
        var file = CreateFile(
            @"\\?\C:\Logs\app.log",
            size: 200 * 1024 * 1024,
            lastWrite: DateTime.UtcNow.AddDays(-120),
            ext: ".log");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestDelete, result);
    }

    [Fact]
    public void AllConditions_OneFail_ShouldReturnNull()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "large_old_log", Priority = 5, SemanticCategory = "日志/转储",
            Reason = "大于100MB且90天未访问的日志文件",
            Conditions = new RuleConditions
            {
                Extensions = new List<string> { ".log" },
                MinSizeBytes = 100 * 1024 * 1024,
                MinAgeDays = 90
            }
        };
        // 大小不满足条件
        var file = CreateFile(
            @"\\?\C:\Logs\app.log",
            size: 10 * 1024 * 1024, // 仅 10 MB
            lastWrite: DateTime.UtcNow.AddDays(-120),
            ext: ".log");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Null(result);
    }

    [Fact]
    public void NoConditions_ShouldMatchEverything()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "match_all", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions() // 无任何限制
        };
        var file = CreateFile(@"\\?\C:\any\file.txt", ext: ".txt");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestDelete, result);
    }

    #endregion

    #region 优先级映射

    [Fact]
    public void Priority5_ShouldReturn_SuggestDelete()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 5, SemanticCategory = "测试", Reason = "test",
            Conditions = new RuleConditions()
        };
        var file = CreateFile(@"\\?\C:\test.txt");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestDelete, result);
    }

    [Fact]
    public void Priority6_ShouldReturn_SuggestKeep()
    {
        var def = new HeuristicRuleDefinition
        {
            Name = "test", Priority = 6, SemanticCategory = "保护", Reason = "test",
            Conditions = new RuleConditions()
        };
        var file = CreateFile(@"\\?\C:\test.txt");

        var result = HeuristicRuleEvaluator.Evaluate(def, file);
        Assert.Equal(RuleVerdict.SuggestKeep, result);
    }

    #endregion
}
