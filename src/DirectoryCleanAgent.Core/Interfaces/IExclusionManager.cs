namespace DirectoryCleanAgent.Core.Interfaces;

/// <summary>
/// 排除管理器接口 — 管理用户排除目录和扩展名的过滤逻辑。
///
/// 在流式处理管道中，排除过滤发生在规则引擎裁决之前（设计文档 7.1 节流程步骤 K）。
/// 排除规则优先级高于所有规则引擎规则（除了硬禁止规则在规则引擎内部处理）。
///
/// 数据来源：
/// - 用户排除目录列表：UserConfig.UserExcludedDirs（支持通配符）
/// - 用户排除扩展名列表：UserConfig.UserExcludedExtensions
/// - 自动保护目录：Docker、WSL、虚拟机等（由规则引擎的保护规则实现）
/// </summary>
public interface IExclusionManager
{
    /// <summary>
    /// 判断指定路径是否被用户排除。
    /// 检查目录匹配（支持通配符，如 "C:\Temp\*"）和扩展名过滤。
    /// </summary>
    /// <param name="filePath">文件完整路径（\\?\ 格式）</param>
    /// <returns>true 表示文件被排除，应跳过后续处理</returns>
    bool IsExcluded(string filePath);

    /// <summary>
    /// 重新加载排除规则。
    /// 在 UserConfig 变更（用户修改排除列表）后调用，刷新内存中的排除模式缓存。
    /// </summary>
    void Reload();
}
