using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Rules.HardRules;

/// <summary>
/// 优先级2：硬自动删除规则集 — 确认安全的系统临时文件，可直接清理。
///
/// 这些规则针对已知的系统临时目录，结合文件年龄阈值（如 >7天）
/// 判断文件是否可安全自动删除。
///
/// 覆盖范围：Windows Temp、用户 Temp、Prefetch、SoftwareDistribution 下载缓存、
/// CBS 日志、回收站等。
/// </summary>
public static class BuiltInAutoDeleteRules
{
    /// <summary>语义分类标签</summary>
    private const string Category = "系统临时文件";

    /// <summary>系统盘符</summary>
    private static readonly string SystemDriveRoot =
        Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

    /// <summary>用户临时目录</summary>
    private static readonly string UserTempPath =
        Path.GetTempPath().TrimEnd('\\');

    /// <summary>
    /// 获取所有内置硬自动删除规则。
    /// </summary>
    public static IReadOnlyList<IRule> All => _rules.Value;

    private static readonly Lazy<IReadOnlyList<IRule>> _rules = new(CreateRules);

    private static IReadOnlyList<IRule> CreateRules()
    {
        var rules = new List<IRule>
        {
            // Windows 临时目录 — 7 天前的文件
            new AgePathAutoDeleteRule(
                "auto_del_windows_temp",
                "Windows 临时文件 (>7天)",
                $@"{SystemDriveRoot}\Windows\Temp",
                minAgeDays: 7),

            // 用户临时目录 — 7 天前的文件
            new AgePathAutoDeleteRule(
                "auto_del_user_temp",
                "用户临时文件 (>7天)",
                UserTempPath,
                minAgeDays: 7),

            // Prefetch 文件 — 30 天前的文件
            new AgePathAutoDeleteRule(
                "auto_del_prefetch",
                "Prefetch 缓存 (>30天)",
                $@"{SystemDriveRoot}\Windows\Prefetch",
                minAgeDays: 30),

            // Windows Update 下载缓存 — 30 天前的文件
            new AgePathAutoDeleteRule(
                "auto_del_wu_download",
                "Windows Update 下载缓存 (>30天)",
                $@"{SystemDriveRoot}\Windows\SoftwareDistribution\Download",
                minAgeDays: 30),

            // CBS 日志文件 — 30 天前的 .log 文件
            new AgePathAutoDeleteRule(
                "auto_del_cbs_logs",
                "CBS 组件服务日志 (>30天)",
                $@"{SystemDriveRoot}\Windows\Logs\CBS",
                minAgeDays: 30,
                extensionFilter: ".log"),

            // 回收站内容 — 全部可自动删除
            new RecycleBinAutoDeleteRule(),

            // Windows Error Reporting 文件 — 30 天前
            new AgePathAutoDeleteRule(
                "auto_del_wer_reports",
                "Windows 错误报告 (>30天)",
                $@"{SystemDriveRoot}\ProgramData\Microsoft\Windows\WER",
                minAgeDays: 30),
        };

        return rules.AsReadOnly();
    }

    // ============================================================
    // 私有规则实现类
    // ============================================================

    /// <summary>
    /// 基于路径前缀 + 文件年龄的自动删除规则。
    /// 文件位于指定目录下且最后写入时间距今超过 minAgeDays 天时触发。
    /// </summary>
    private sealed class AgePathAutoDeleteRule : IRule
    {
        private readonly string _targetPathUpper;
        private readonly int _minAgeDays;
        private readonly string? _extensionFilter;

        public int Priority => 2;
        public string RuleName { get; }
        public string SemanticCategory => Category;
        public string Reason { get; }

        public AgePathAutoDeleteRule(
            string ruleName,
            string reason,
            string targetPath,
            int minAgeDays,
            string? extensionFilter = null)
        {
            RuleName = ruleName;
            Reason = reason;
            _targetPathUpper = targetPath.ToUpperInvariant();
            _minAgeDays = minAgeDays;
            _extensionFilter = extensionFilter?.ToLowerInvariant();
        }

        public RuleVerdict? Evaluate(FileItem file, UserConfig config)
        {
            var filePathUpper = file.FilePath.ToUpperInvariant();

            // 路径匹配：文件路径以目标目录开头
            bool pathMatch = filePathUpper.Contains(_targetPathUpper);

            if (!pathMatch)
                return null;

            // 扩展名过滤（可选）
            if (_extensionFilter != null)
            {
                var ext = file.Extension?.ToLowerInvariant();
                if (ext != _extensionFilter)
                    return null;
            }

            // 年龄判断：LastWriteTime 距今不足 minAgeDays 则不触发
            var age = DateTime.UtcNow - file.LastWriteTime;
            if (age.TotalDays < _minAgeDays)
                return null;

            return RuleVerdict.AutoDelete;
        }
    }

    /// <summary>
    /// 回收站自动删除规则：匹配所有卷的 $Recycle.Bin 目录下文件。
    /// </summary>
    private sealed class RecycleBinAutoDeleteRule : IRule
    {
        public int Priority => 2;
        public string RuleName => "auto_del_recycle_bin";
        public string SemanticCategory => "回收站";
        public string Reason => "回收站中的文件";

        public RuleVerdict? Evaluate(FileItem file, UserConfig config)
        {
            var filePathUpper = file.FilePath.ToUpperInvariant();
            // $Recycle.Bin 是回收站的标准目录名
            if (filePathUpper.Contains(@"\$RECYCLE.BIN\", StringComparison.Ordinal))
                return RuleVerdict.AutoDelete;
            // 也匹配旧式名 RECYCLER
            if (filePathUpper.Contains(@"\RECYCLER\", StringComparison.Ordinal))
                return RuleVerdict.AutoDelete;
            return null;
        }
    }
}
