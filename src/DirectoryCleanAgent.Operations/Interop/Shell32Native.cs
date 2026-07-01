using System.Runtime.InteropServices;

namespace DirectoryCleanAgent.Operations.Interop;

/// <summary>
/// Windows Shell32 + Kernel32 API 的 P/Invoke 封装。
///
/// 设计原则（与 EverythingNative 一致）：
/// - 手动 P/Invoke，不引入 Vanara 等重型 NuGet 包。
/// - 所有路径操作使用 \\?\ 前缀格式。
/// - SHQueryRecycleBin 在删除操作前仅调用一次（快速失败原则）。
///
/// 覆盖的 API：
/// - SHQueryRecycleBinW：回收站容量查询（Vista+）
/// - GetVolumeInformationW：判断卷序列号，用于跨卷检测
/// - MoveFileExW：文件重命名/移动（用于 .cleaning.tmp → .quarantine 重命名）
/// </summary>
internal static partial class Shell32Native
{
    private const string Shell32 = "shell32.dll";
    private const string Kernel32 = "kernel32.dll";

    // ================================================================
    // SHQueryRecycleBin — 回收站容量查询
    // ================================================================

    /// <summary>
    /// SHQUERYRBINFO 结构体，用于接收回收站容量信息。
    /// 对应 Win32 SHQUERYRBINFO 结构。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SHQUERYRBINFO
    {
        /// <summary>结构体大小，调用前必须初始化为 sizeof(SHQUERYRBINFO)</summary>
        public int cbSize;

        /// <summary>回收站中所有对象的总大小（字节）</summary>
        public long i64Size;

        /// <summary>回收站中所有对象的总数量</summary>
        public long i64NumItems;
    }

    /// <summary>
    /// 查询回收站中所有对象的总体大小和数量。
    ///
    /// 参数：
    /// - pszRootPath: 驱动器根路径（如 "C:\"），传 null 查询所有驱动器的回收站。
    /// - pSHQueryRBInfo: 输入/输出结构体，cbSize 必须预先设置。
    ///
    /// 返回值：
    /// - S_OK (0): 成功
    /// - 其他 HRESULT: 失败
    ///
    /// 设计文档 7.2 节：此函数在快照生成之前调用，且仅调用一次。
    /// </summary>
    [DllImport(Shell32, CharSet = CharSet.Unicode)]
    internal static extern int SHQueryRecycleBinW(
        string? pszRootPath,
        ref SHQUERYRBINFO pSHQueryRBInfo);

    // ================================================================
    // GetVolumeInformation — 卷信息查询（跨卷检测）
    // ================================================================

    /// <summary>
    /// 获取指定卷的序列号，用于判断两个路径是否位于同一卷。
    ///
    /// 跨卷检测逻辑：
    /// - 源文件和目标路径分别调用此函数获取卷序列号。
    /// - 序列号相同 → 同卷，可直接 MoveFile。
    /// - 序列号不同 → 跨卷，需 CopyFile + 校验 + Delete 流程。
    /// </summary>
    /// <param name="lpRootPathName">卷根路径（如 "C:\"）</param>
    /// <param name="lpVolumeNameBuffer">卷名缓冲区（不需要时传 null）</param>
    /// <param name="nVolumeNameSize">卷名缓冲区大小</param>
    /// <param name="lpVolumeSerialNumber">输出：卷序列号</param>
    /// <param name="lpMaximumComponentLength">输出：最大文件名长度</param>
    /// <param name="lpFileSystemFlags">输出：文件系统特性标志</param>
    /// <param name="lpFileSystemNameBuffer">文件系统名缓冲区（不需要时传 null）</param>
    /// <param name="nFileSystemNameSize">文件系统名缓冲区大小</param>
    /// <returns>成功返回 true，失败返回 false</returns>
    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformationW(
        string lpRootPathName,
        IntPtr lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out int lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        IntPtr lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    // ================================================================
    // MoveFileEx — 文件重命名/移动（用于 .cleaning.tmp 重命名）
    // ================================================================

    /// <summary>MOVEFILE_REPLACE_EXISTING: 若目标已存在则覆盖</summary>
    internal const uint MOVEFILE_REPLACE_EXISTING = 0x1;

    /// <summary>MOVEFILE_WRITE_THROUGH: 立即写入磁盘，不缓存</summary>
    internal const uint MOVEFILE_WRITE_THROUGH = 0x8;

    /// <summary>
    /// 移动/重命名文件。
    /// 用于隔离区流程中 .cleaning.tmp → .quarantine 的原子重命名操作。
    ///
    /// 注意：\\?\ 前缀路径下不支持 MOVEFILE_DELAY_UNTIL_REBOOT，
    /// 且跨卷移动会失败（与 .NET File.Move 行为一致）。
    /// </summary>
    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MoveFileExW(
        string lpExistingFileName,
        string lpNewFileName,
        uint dwFlags);

    // ================================================================
    // 错误码常量（与 IFileOperation COM 共享）
    // ================================================================

    /// <summary>ERROR_SHARING_VIOLATION — 文件被其他进程占用</summary>
    internal const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);

    /// <summary>ERROR_LOCK_VIOLATION — 文件被锁定</summary>
    internal const int ERROR_LOCK_VIOLATION = unchecked((int)0x80070021);

    /// <summary>ERROR_ACCESS_DENIED — 权限不足</summary>
    internal const int ERROR_ACCESS_DENIED = unchecked((int)0x80070005);

    /// <summary>ERROR_FILE_NOT_FOUND — 文件不存在</summary>
    internal const int ERROR_FILE_NOT_FOUND = unchecked((int)0x80070002);

    /// <summary>S_OK — 操作成功</summary>
    internal const int S_OK = 0;
}
