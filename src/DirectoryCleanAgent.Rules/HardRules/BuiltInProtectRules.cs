using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Rules.HardRules;

/// <summary>
/// 优先级4：保护规则集 — 自动检测开发环境（Docker、WSL、虚拟机）并保护其数据文件。
///
/// 这些规则在用户排除规则（优先级3）之后执行，保护常见的开发/虚拟化环境数据
/// 不被误删。用户可以通过排除规则进一步自定义保护范围。
///
/// 优先级4的规则返回 Protect verdict，决策引擎会将其映射为 final_action = Protected。
/// </summary>
public static class BuiltInProtectRules
{
    /// <summary>语义分类标签</summary>
    private const string Category = "保护内容";

    /// <summary>
    /// 获取所有内置保护规则。
    /// </summary>
    public static IReadOnlyList<IRule> All => _rules.Value;

    private static readonly Lazy<IReadOnlyList<IRule>> _rules = new(CreateRules);

    private static IReadOnlyList<IRule> CreateRules()
    {
        var rules = new List<IRule>
        {
            // Docker 数据目录
            new PathProtectRule(
                "protect_docker_data",
                "Docker 容器与镜像数据",
                new[] { @"\ProgramData\Docker", @"\ProgramData\docker" }),

            // Docker Desktop 虚拟磁盘
            new ExtensionPathProtectRule(
                "protect_docker_vhdx",
                "Docker Desktop 虚拟磁盘",
                new[] { @"\ProgramData\DockerDesktop" },
                new[] { ".vhdx" }),

            // WSL 发行版路径（通常位于用户目录下的隐藏文件夹）
            new PathProtectRule(
                "protect_wsl_distros",
                "WSL 发行版数据",
                new[] { @"\Packages\CanonicalGroupLimited", @"\AppData\Local\Docker\wsl" }),

            // WSL 虚拟磁盘映像
            new ExtensionProtectRule(
                "protect_wsl_vhdx",
                "WSL 虚拟磁盘映像 (.vhdx)",
                ".vhdx",
                // 仅当 vhdx 位于 WSL 相关路径时才保护
                pathMustContain: "wsl"),

            // 虚拟机磁盘文件（VirtualBox、VMware、Hyper-V）
            new ExtensionProtectRule(
                "protect_vm_disk_vdi",
                "VirtualBox 虚拟磁盘 (.vdi)",
                ".vdi"),

            new ExtensionProtectRule(
                "protect_vm_disk_vmdk",
                "VMware 虚拟磁盘 (.vmdk)",
                ".vmdk"),

            new ExtensionProtectRule(
                "protect_vm_disk_vhd",
                "Hyper-V 虚拟磁盘 (.vhd)",
                ".vhd"),

            new ExtensionProtectRule(
                "protect_vm_disk_vhdx",
                "Hyper-V 虚拟磁盘 (.vhdx)",
                ".vhdx"),

            // 虚拟机配置与快照目录
            new PathProtectRule(
                "protect_vm_dirs",
                "虚拟机配置与快照",
                new[] { @"\VirtualBox VMs", @"\Virtual Machines", @"\VMware" }),
        };

        return rules.AsReadOnly();
    }

    // ============================================================
    // 私有规则实现类
    // ============================================================

    /// <summary>
    /// 基于路径的通用保护规则：文件路径包含任一受保护目录路径则触发。
    /// </summary>
    private sealed class PathProtectRule : IRule
    {
        private readonly string[] _protectedPathsUpper;

        public int Priority => 4;
        public string RuleName { get; }
        public string SemanticCategory => Category;
        public string Reason { get; }

        public PathProtectRule(string ruleName, string reason, string[] protectedPaths)
        {
            RuleName = ruleName;
            Reason = reason;
            _protectedPathsUpper = protectedPaths.Select(p => p.ToUpperInvariant()).ToArray();
        }

        public RuleVerdict? Evaluate(FileItem file, UserConfig config)
        {
            var filePathUpper = file.FilePath.ToUpperInvariant();
            foreach (var protectedPath in _protectedPathsUpper)
            {
                if (filePathUpper.Contains(protectedPath, StringComparison.Ordinal))
                    return RuleVerdict.Protect;
            }
            return null;
        }
    }

    /// <summary>
    /// 基于扩展名的通用保护规则：匹配指定扩展名则触发。
    /// </summary>
    private sealed class ExtensionProtectRule : IRule
    {
        private readonly string _extensionLower;
        private readonly string? _pathMustContainLower;

        public int Priority => 4;
        public string RuleName { get; }
        public string SemanticCategory => Category;
        public string Reason { get; }

        public ExtensionProtectRule(string ruleName, string reason, string extension, string? pathMustContain = null)
        {
            RuleName = ruleName;
            Reason = reason;
            _extensionLower = extension.ToLowerInvariant();
            _pathMustContainLower = pathMustContain?.ToLowerInvariant();
        }

        public RuleVerdict? Evaluate(FileItem file, UserConfig config)
        {
            // 扩展名匹配
            if (file.Extension?.ToLowerInvariant() != _extensionLower)
                return null;

            // 可选的路径约束
            if (_pathMustContainLower != null)
            {
                if (!file.FilePath.ToLowerInvariant().Contains(_pathMustContainLower, StringComparison.Ordinal))
                    return null;
            }

            return RuleVerdict.Protect;
        }
    }

    /// <summary>
    /// 基于路径 + 扩展名的组合保护规则：文件同时满足路径和扩展名条件才触发。
    /// </summary>
    private sealed class ExtensionPathProtectRule : IRule
    {
        private readonly string[] _pathsUpper;
        private readonly HashSet<string> _extensionsLower;

        public int Priority => 4;
        public string RuleName { get; }
        public string SemanticCategory => Category;
        public string Reason { get; }

        public ExtensionPathProtectRule(string ruleName, string reason, string[] paths, string[] extensions)
        {
            RuleName = ruleName;
            Reason = reason;
            _pathsUpper = paths.Select(p => p.ToUpperInvariant()).ToArray();
            _extensionsLower = new HashSet<string>(
                extensions.Select(e => e.ToLowerInvariant()),
                StringComparer.Ordinal);
        }

        public RuleVerdict? Evaluate(FileItem file, UserConfig config)
        {
            // 扩展名检查
            if (file.Extension == null || !_extensionsLower.Contains(file.Extension.ToLowerInvariant()))
                return null;

            // 路径检查
            var filePathUpper = file.FilePath.ToUpperInvariant();
            foreach (var path in _pathsUpper)
            {
                if (filePathUpper.Contains(path, StringComparison.Ordinal))
                    return RuleVerdict.Protect;
            }
            return null;
        }
    }
}
