using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Rules.HardRules;

/// <summary>
/// 优先级1：硬禁止规则集 — 保护系统关键目录，绝对不可触碰。
///
/// 这些规则编译在代码中，不可通过配置文件修改或禁用，
/// 确保即使用户误操作也不会删除系统核心文件。
///
/// 覆盖范围：System32、SysWOW64、WinSxS、Boot、EFI、系统页面文件等。
/// </summary>
public static class BuiltInForbidRules
{
    /// <summary>语义分类标签</summary>
    private const string Category = "保护内容";

    /// <summary>
    /// 获取系统盘符（如 "C:"），用于动态构建系统保护路径。
    /// </summary>
    private static readonly string SystemDriveRoot =
        Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

    /// <summary>
    /// 获取所有内置硬禁止规则。
    /// 每个规则返回一个 IRule 实例，按优先级 1 执行。
    /// </summary>
    public static IReadOnlyList<IRule> All => _rules.Value;

    private static readonly Lazy<IReadOnlyList<IRule>> _rules = new(CreateRules);

    private static IReadOnlyList<IRule> CreateRules()
    {
        // 受保护的系统目录列表
        var protectedDirs = new[]
        {
            // Windows 核心目录
            @"\Windows\System32",
            @"\Windows\SysWOW64",
            @"\Windows\System",
            @"\Windows\WinSxS",
            @"\Windows\servicing",
            @"\Windows\Assembly",
            @"\Windows\Microsoft.NET",
            @"\Windows\IME",
            @"\Windows\CSC",
            // 启动与引导
            @"\Boot",
            @"\EFI",
            // 系统卷信息
            @"\System Volume Information",
            // Program Files 下的系统组件
            @"\Program Files\Common Files",
            @"\Program Files (x86)\Common Files",
            // Windows Defender / 安全
            @"\ProgramData\Microsoft\Windows Defender",
        };

        var rules = new List<IRule>(protectedDirs.Length + 3);

        foreach (var dir in protectedDirs)
        {
            // 为每个受保护目录创建一条单独规则，方便 UI 展示具体匹配的规则名
            var dirName = dir.Replace('\\', '_').TrimStart('_');
            var fullPath = SystemDriveRoot + dir;
            rules.Add(new PathForbidRule(
                $"forbid_{dirName.ToLowerInvariant()}",
                $"系统目录: {fullPath}",
                fullPath));
        }

        // 系统页面文件、休眠文件、交换文件（位于系统盘根目录）
        rules.Add(new FileNameForbidRule(
            "forbid_system_page_files",
            "系统页面/休眠/交换文件",
            SystemDriveRoot + "\\",
            new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys" }));

        // Windows 注册表文件
        rules.Add(new PathForbidRule(
            "forbid_registry_hives",
            "Windows 注册表配置单元",
            SystemDriveRoot + @"\Windows\System32\config"));

        return rules.AsReadOnly();
    }

    // ============================================================
    // 私有规则实现类
    // ============================================================

    /// <summary>
    /// 基于路径前缀的禁止规则：文件路径以指定前缀开头则禁止。
    /// </summary>
    private sealed class PathForbidRule : IRule
    {
        private readonly string _forbiddenPathUpper; // 预计算的大写路径前缀，避免每次比较时转换

        public int Priority => 1;
        public string RuleName { get; }
        public string SemanticCategory => Category;
        public string Reason { get; }

        public PathForbidRule(string ruleName, string reason, string forbiddenPath)
        {
            RuleName = ruleName;
            Reason = reason;
            // 同时支持普通格式和 \\?\ 格式
            _forbiddenPathUpper = forbiddenPath.ToUpperInvariant();
        }

        public RuleVerdict? Evaluate(FileItem file, UserConfig config)
        {
            // FileItem.FilePath 是 \\?\ 格式，去掉前缀后再比较
            var filePathUpper = file.FilePath.ToUpperInvariant();
            // 检查 \\?\ 格式
            if (filePathUpper.StartsWith(@"\\?\" + _forbiddenPathUpper, StringComparison.Ordinal))
                return RuleVerdict.Forbid;
            // 也检查无前缀格式（以防万一）
            if (filePathUpper.StartsWith(_forbiddenPathUpper, StringComparison.Ordinal))
                return RuleVerdict.Forbid;
            return null;
        }
    }

    /// <summary>
    /// 基于文件名的禁止规则：文件名在禁止列表中则禁止。
    /// </summary>
    private sealed class FileNameForbidRule : IRule
    {
        private readonly string _directoryPathUpper;
        private readonly HashSet<string> _forbiddenNamesUpper;

        public int Priority => 1;
        public string RuleName { get; }
        public string SemanticCategory => Category;
        public string Reason { get; }

        public FileNameForbidRule(string ruleName, string reason, string directoryPath, string[] forbiddenNames)
        {
            RuleName = ruleName;
            Reason = reason;
            _directoryPathUpper = directoryPath.ToUpperInvariant();
            _forbiddenNamesUpper = new HashSet<string>(
                forbiddenNames.Select(n => n.ToUpperInvariant()),
                StringComparer.Ordinal);
        }

        public RuleVerdict? Evaluate(FileItem file, UserConfig config)
        {
            var filePathUpper = file.FilePath.ToUpperInvariant();
            // 提取文件名
            var fileName = Path.GetFileName(filePathUpper);
            if (string.IsNullOrEmpty(fileName))
                return null;

            // 检查文件所在目录和文件名
            var dirPath = Path.GetDirectoryName(filePathUpper);
            if (dirPath == null)
                return null;

            // 确保路径以 \\?\ 开头检查
            var dirPathNormalized = dirPath;
            if (!dirPathNormalized.EndsWith('\\'))
                dirPathNormalized += "\\";

            // 匹配目录（支持 \\?\ 格式和普通格式）
            var targetDirUpper = _directoryPathUpper;
            if (!targetDirUpper.EndsWith('\\'))
                targetDirUpper += "\\";

            bool dirMatch = dirPathNormalized.EndsWith(targetDirUpper, StringComparison.Ordinal)
                         || dirPathNormalized == targetDirUpper;

            if (dirMatch && _forbiddenNamesUpper.Contains(fileName))
                return RuleVerdict.Forbid;

            return null;
        }
    }
}
