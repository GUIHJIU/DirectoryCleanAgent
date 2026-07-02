namespace DirectoryCleanAgent.Everything;

/// <summary>
/// Everything 安装状态检测结果。
/// </summary>
public enum EverythingInstallStatus
{
    /// <summary>检测尚未运行</summary>
    Unknown,
    /// <summary>Everything 已在系统上安装</summary>
    Installed,
    /// <summary>Everything 未安装</summary>
    NotInstalled,
}

/// <summary>
/// Everything 依赖检测完整结果。
///
/// 封装 5 步启动检测序列的所有输出，供 UI 层（首次启动向导 / 主窗口）决策后续行为。
/// 检测步骤：进程存活 → 版本检查 → IPC ping → FRN 能力探测 → 索引就绪检测。
///
/// 前 3 步任一失败则阻止进入主界面（阻断式）；
/// FRN 探测失败仅标记降级（非阻断）；
/// 索引未就绪提供等待/后台等待/跳过选项（非阻断）。
/// </summary>
public class EverythingDetectionResult
{
    // ================================================================
    // 步骤1：进程存活
    // ================================================================

    /// <summary>Everything 进程是否正在运行</summary>
    public bool ProcessAlive { get; init; }

    // ================================================================
    // 步骤2：版本检查
    // ================================================================

    /// <summary>
    /// Everything SDK 原始版本号（SDK 2.0 的 Everything_GetVersion 返回值）。
    /// SDK 3.0 不再使用编码版本号，此值为 0。
    /// </summary>
    public uint RawVersion { get; init; }

    /// <summary>主版本号</summary>
    public int MajorVersion { get; init; }

    /// <summary>次版本号</summary>
    public int MinorVersion { get; init; }

    /// <summary>修订号</summary>
    public int Revision { get; init; }

    /// <summary>构建号</summary>
    public int BuildNumber { get; init; }

    /// <summary>版本号是否满足最低要求（≥ 1.4.1）</summary>
    public bool VersionOk { get; init; }

    /// <summary>格式化的版本字符串（如 "1.4.1.232"）</summary>
    public string VersionString => $"{MajorVersion}.{MinorVersion}.{Revision}.{BuildNumber}";

    // ================================================================
    // 步骤3：IPC 通信
    // ================================================================

    /// <summary>IPC ping 是否成功（Everything 服务可正常通信）</summary>
    public bool IpcPingOk { get; init; }

    // ================================================================
    // 步骤4：FRN 能力
    // ================================================================

    /// <summary>
    /// FRN（File Reference Number）是否可用。
    /// false 时墓碑匹配降级为 Size+LastWriteTime 指纹模式，强制 3 天过期。
    /// </summary>
    public bool FRN_AVAILABLE { get; init; }

    // ================================================================
    // 步骤5：索引就绪
    // ================================================================

    /// <summary>Everything 数据库是否已加载</summary>
    public bool DBLoaded { get; init; }

    /// <summary>Everything 是否正在构建索引（true=索引未就绪，结果可能不完整）</summary>
    public bool IsIndexing { get; init; }

    // ================================================================
    // 汇总信息
    // ================================================================

    /// <summary>
    /// 前 3 步阻断式检测是否全部通过。
    /// true 表示可以进入主界面（可能需要等待索引或降级 FRN）；
    /// false 表示必须阻断并引导用户处理。
    /// </summary>
    public bool CanProceedToMainUI => ProcessAlive && VersionOk && IpcPingOk;

    /// <summary>检测失败时的详细原因描述（供 UI 展示给用户）</summary>
    public string? FailureReason { get; init; }

    /// <summary>检测完成的时间戳（UTC）</summary>
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

    /// <summary>检测总耗时（毫秒）</summary>
    public long ElapsedMilliseconds { get; init; }

    /// <summary>
    /// 已连接的 Everything 客户端句柄。
    /// SDK 3.0：检测通过后，调用方可将此句柄传递给 Everything3SdkWrapper 以复用连接。
    /// SDK 2.0：始终为 IntPtr.Zero（无客户端句柄概念）。
    /// </summary>
    public IntPtr ClientHandle { get; init; }

    /// <summary>
    /// 检测到的 SDK 版本号（2 = SDK 2.0 / Everything 1.4+, 3 = SDK 3.0 / Everything 1.5+）。
    /// </summary>
    public int SdkVersion { get; init; }

    // ================================================================
    // 安装状态（供向导 Step0 决定是否展示安装界面）
    // ================================================================

    /// <summary>Everything 在系统上的安装状态</summary>
    public EverythingInstallStatus InstallStatus { get; init; } = EverythingInstallStatus.Unknown;

    /// <summary>建议用户执行的操作（install/start/upgrade/null）</summary>
    public string? InstallSuggestion { get; init; }

    /// <summary>Everything.exe 在系统上的路径（若找到）</summary>
    public string? EverythingExePath { get; init; }

    /// <summary>创建成功结果的工厂方法</summary>
    public static EverythingDetectionResult CreateSuccess(
        uint rawVersion, int major, int minor, int rev, int build,
        bool frnAvailable, bool isIndexing, long elapsedMs)
    {
        return new EverythingDetectionResult
        {
            ProcessAlive = true,
            RawVersion = rawVersion,
            MajorVersion = major,
            MinorVersion = minor,
            Revision = rev,
            BuildNumber = build,
            VersionOk = true,
            IpcPingOk = true,
            FRN_AVAILABLE = frnAvailable,
            DBLoaded = true,
            IsIndexing = isIndexing,
            ElapsedMilliseconds = elapsedMs
        };
    }

    /// <summary>创建失败结果的工厂方法（阻断式检测失败时使用）</summary>
    public static EverythingDetectionResult CreateFailure(
        string reason, long elapsedMs = 0,
        EverythingInstallStatus installStatus = EverythingInstallStatus.Unknown,
        string? installSuggestion = null,
        string? exePath = null)
    {
        return new EverythingDetectionResult
        {
            ProcessAlive = false,
            VersionOk = false,
            IpcPingOk = false,
            FRN_AVAILABLE = false,
            DBLoaded = false,
            IsIndexing = true,
            FailureReason = reason,
            ElapsedMilliseconds = elapsedMs,
            InstallStatus = installStatus,
            InstallSuggestion = installSuggestion,
            EverythingExePath = exePath,
        };
    }
}
