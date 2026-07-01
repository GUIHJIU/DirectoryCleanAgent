using System.Text.Json.Serialization;

namespace DirectoryCleanAgent.Core.Config;

/// <summary>
/// 用户配置根对象，涵盖通用、扫描、AI、排除、模式五大配置节。
/// 所有字段提供合理默认值，可通过 JSON 序列化/反序列化与配置文件同步。
/// </summary>
public class UserConfig
{
    // ============================================================
    // 通用设置（设计文档 3.7 节 - 标签页1）
    // ============================================================

    /// <summary>启动时行为：自动扫描 或 显示首页</summary>
    [JsonPropertyName("startupBehavior")]
    public StartupBehavior StartupBehavior { get; set; } = StartupBehavior.AutoScan;

    /// <summary>UI 主题：浅色 / 深色 / 跟随系统</summary>
    [JsonPropertyName("theme")]
    public ThemeType Theme { get; set; } = ThemeType.FollowSystem;

    /// <summary>界面语言：zh-CN（简体中文）或 en-US</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh-CN";

    /// <summary>数据存储根目录路径，默认 %LocalAppData%\SpaceClear</summary>
    [JsonPropertyName("dataStoragePath")]
    public string DataStoragePath { get; set; } = "";

    // ============================================================
    // 扫描与清理设置（设计文档 3.7 节 - 标签页2）
    // ============================================================

    /// <summary>扫描模式：智能扫描系统盘 或 每次询问目录</summary>
    [JsonPropertyName("scanMode")]
    public ScanMode ScanMode { get; set; } = ScanMode.SmartScanSystemDrive;

    /// <summary>安全删除方式：放入回收站（默认）或 永久删除</summary>
    [JsonPropertyName("deleteMethod")]
    public DeleteMethod DeleteMethod { get; set; } = DeleteMethod.RecycleBin;

    /// <summary>一键清理前是否弹出二次确认对话框</summary>
    [JsonPropertyName("confirmBeforeOneClickClean")]
    public bool ConfirmBeforeOneClickClean { get; set; } = true;

    /// <summary>是否显示"需人工确认"类别的文件</summary>
    [JsonPropertyName("showManualReviewFiles")]
    public bool ShowManualReviewFiles { get; set; } = true;

    /// <summary>是否自动刷新扫描结果（Everyting 事件驱动）</summary>
    [JsonPropertyName("autoRefreshScanResults")]
    public bool AutoRefreshScanResults { get; set; } = true;

    /// <summary>包含的磁盘卷（仅 NTFS 卷），默认仅勾选系统盘</summary>
    [JsonPropertyName("includedVolumes")]
    public List<string> IncludedVolumes { get; set; } = new();

    /// <summary>
    /// 单次扫描最大文件数上限，默认 100000。
    /// 控制 B1→B2→B3 管道处理的最大文件数量，防止百万级文件扫描耗时过长。
    /// 设为 0 表示不限制（仅在明确需要全量扫描时使用）。
    /// </summary>
    [JsonPropertyName("maxScanFiles")]
    public int MaxScanFiles { get; set; } = 100_000;

    // ============================================================
    // AI 顾问设置（设计文档 3.7 节 - 标签页3）
    // ============================================================

    /// <summary>是否启用 AI 顾问功能</summary>
    [JsonPropertyName("aiEnabled")]
    public bool AIEnabled { get; set; } = false;

    /// <summary>AI 服务类型：openai / ollama / custom</summary>
    [JsonPropertyName("aiServiceType")]
    public string AIServiceType { get; set; } = "custom";

    /// <summary>AI API 服务地址（URL）</summary>
    [JsonPropertyName("aiApiUrl")]
    public string AIApiUrl { get; set; } = "";

    /// <summary>AI API 密钥（加密存储，明文仅在内存中）</summary>
    [JsonPropertyName("aiApiKey")]
    public string AIApiKey { get; set; } = "";

    /// <summary>AI 模型名称</summary>
    [JsonPropertyName("aiModel")]
    public string AIModel { get; set; } = "gpt-3.5-turbo";

    /// <summary>AI 信任度：低 / 中 / 高</summary>
    [JsonPropertyName("aiTrustLevel")]
    public AITrustLevel AITrustLevel { get; set; } = AITrustLevel.Medium;

    /// <summary>AI 每日调用次数限制</summary>
    [JsonPropertyName("aiDailyLimit")]
    public int AIDailyLimit { get; set; } = 50;

    /// <summary>是否自动为未分类文件请求 AI 分析（仅专家模式生效）</summary>
    [JsonPropertyName("aiAutoAnalyze")]
    public bool AIAutoAnalyze { get; set; } = false;

    // ============================================================
    // 排除与保护设置（设计文档 3.7 节 - 标签页5）
    // ============================================================

    /// <summary>用户手动添加的排除目录列表（支持通配符）</summary>
    [JsonPropertyName("userExcludedDirs")]
    public List<string> UserExcludedDirs { get; set; } = new();

    /// <summary>用户排除的文件扩展名列表（含点号，如 ".log"）</summary>
    [JsonPropertyName("userExcludedExtensions")]
    public List<string> UserExcludedExtensions { get; set; } = new();

    /// <summary>隔离区最大容量（字节），默认 10 GB</summary>
    [JsonPropertyName("quarantineMaxSizeBytes")]
    public long QuarantineMaxSizeBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>隔离区文件保留天数，默认 30 天</summary>
    [JsonPropertyName("quarantineRetentionDays")]
    public int QuarantineRetentionDays { get; set; } = 30;

    // ============================================================
    // 系统运行时标记（持久化，但不面向用户编辑）
    // ============================================================

    /// <summary>规则缓存版本号。
    /// 由 FileSystemWatcher（500ms 防抖）或内置编辑器保存操作自动递增。
    /// 启动时若 FileDecisionCache.cache_version 与此值不一致，则清空缓存并重扫。
    /// </summary>
    [JsonPropertyName("ruleCacheVersion")]
    public int RuleCacheVersion { get; set; } = 0;

    /// <summary>FRN（File Reference Number）能力标记。
    /// 启动时通过探测 C:\Windows\explorer.exe 的 FRN 字段获取。
    /// false 时墓碑匹配降级为 Size+LastWriteTime 指纹模式，强制 3 天过期。
    /// </summary>
    [JsonPropertyName("frnAvailable")]
    public bool FRN_AVAILABLE { get; set; } = true;

    // ============================================================
    // 用户画像（配置向导步骤2，仅专家模式展示）
    // ============================================================

    /// <summary>使用模式：简单模式（推荐）或 专家模式</summary>
    [JsonPropertyName("userMode")]
    public UserMode UserMode { get; set; } = UserMode.Simple;

    /// <summary>用户职业（专家模式配置向导收集）</summary>
    [JsonPropertyName("userProfession")]
    public string UserProfession { get; set; } = "";

    /// <summary>设备主要用途（专家模式配置向导收集）</summary>
    [JsonPropertyName("deviceUsage")]
    public string DeviceUsage { get; set; } = "";

    // ============================================================
    // 向导运行时标记
    // ============================================================

    /// <summary>
    /// 是否为首次运行，默认 true。
    /// 首次运行时显示配置向导，向导完成或用户跳过后设为 false。
    /// 若为 false，App 启动时直接进入主界面。
    /// </summary>
    [JsonPropertyName("isFirstRun")]
    public bool IsFirstRun { get; set; } = true;

    // ============================================================
    // 工具方法
    // ============================================================

    /// <summary>
    /// 创建当前配置的深拷贝副本。
    /// 调用方可安全地读取和修改副本，不会影响 JsonConfigService 内部的可变实例。
    /// </summary>
    /// <returns>独立的深拷贝 UserConfig 实例</returns>
    public UserConfig Clone()
    {
        // MemberwiseClone 对值类型(int/bool/long/enum)和不可变引用类型(string)做浅拷贝即可
        var clone = (UserConfig)MemberwiseClone();
        // 三个 List<string> 必须深拷贝，避免与原实例共享可变集合
        clone.IncludedVolumes = new List<string>(IncludedVolumes);
        clone.UserExcludedDirs = new List<string>(UserExcludedDirs);
        clone.UserExcludedExtensions = new List<string>(UserExcludedExtensions);
        return clone;
    }
}
