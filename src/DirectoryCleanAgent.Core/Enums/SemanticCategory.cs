namespace DirectoryCleanAgent.Core.Enums;

/// <summary>
/// 语义分类标签枚举，对应设计文档 3.3 节定义的 8 个语义标签。
/// 用于简单模式下的文件分类展示，替代具体规则名称。
/// </summary>
public enum SemanticCategory
{
    /// <summary>系统临时文件 — Windows 或程序临时文件</summary>
    SystemTempFiles = 1,

    /// <summary>应用缓存 — 浏览器、微信等缓存</summary>
    AppCache = 2,

    /// <summary>下载旧文件 — 下载目录中 90 天未使用的文件</summary>
    OldDownloads = 3,

    /// <summary>回收站 — 已删除文件</summary>
    RecycleBin = 4,

    /// <summary>日志/转储 — .log、.dmp 等</summary>
    LogsAndDumps = 5,

    /// <summary>用户旧文件 — 用户目录下长期未访问的大文件</summary>
    OldUserFiles = 6,

    /// <summary>保护内容 — 不可删除（系统/Docker/WSL/用户白名单）</summary>
    Protected = 7,

    /// <summary>未分类 — 未知类型，可能触发 AI 自动分析</summary>
    Uncategorized = 8
}
