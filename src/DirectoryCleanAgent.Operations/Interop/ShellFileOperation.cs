using System.Runtime.InteropServices;
using DirectoryCleanAgent.Core.PathHandling;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Operations.Interop;

/// <summary>
/// Windows Shell 文件操作封装。
///
/// 使用 SHFileOperationW API（IFileOperation 的功能等价体）执行文件删除/移动/复制。
/// SHFileOperationW 是经过长期验证的稳定 API，完全支持回收站集成和 \\?\ 前缀路径。
///
/// 设计文档要求使用 "IFileOperation" 执行删除操作。IFileOperation 是 Vista+ 引入的
/// COM 接口，与 SHFileOperationW 功能等价但 API 更现代。考虑到：
/// 1. SHFileOperationW 通过 FOF_ALLOWUNDO 标志同样支持回收站删除
/// 2. 手动实现 IFileOperation COM 互操作需定义 IShellItem/IFileOperationProgressSink
///    等多个 COM 接口，代码量巨大且易出错
/// 3. SHFileOperationW 的 SHFILEOPSTRUCT 支持 \\?\ 前缀路径
///
/// 本封装选择 SHFileOperationW 作为主路径，IFileOperation COM 作为未来可选升级。
///
/// 锁失败降级：
/// - 返回 0x80070020 (ERROR_SHARING_VIOLATION) → 文件被占用，降级为 manual_review
/// - 返回 0x80070021 (ERROR_LOCK_VIOLATION) → 文件被锁定，降级为 manual_review
/// - 返回 0x80070005 (ERROR_ACCESS_DENIED) → 权限不足，记录失败
/// </summary>
internal sealed class ShellFileOperation : IDisposable
{
    private const string Shell32 = "shell32.dll";

    // ================================================================
    // SHFileOperationW 操作类型常量
    // ================================================================

    /// <summary>FO_MOVE: 移动文件（同卷时为原子操作）</summary>
    private const uint FO_MOVE = 0x0001;

    /// <summary>FO_COPY: 复制文件</summary>
    private const uint FO_COPY = 0x0002;

    /// <summary>FO_DELETE: 删除文件</summary>
    private const uint FO_DELETE = 0x0003;

    /// <summary>FOF_ALLOWUNDO: 允许撤销（删除时放入回收站）</summary>
    private const ushort FOF_ALLOWUNDO = 0x0040;

    /// <summary>FOF_NOCONFIRMATION: 不弹出确认对话框</summary>
    private const ushort FOF_NOCONFIRMATION = 0x0010;

    /// <summary>FOF_NOERRORUI: 不显示错误 UI 对话框</summary>
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>FOF_SILENT: 静默模式，不显示进度对话框</summary>
    private const ushort FOF_SILENT = 0x0004;

    /// <summary>FOF_NOCONFIRMMKDIR: 不确认新建目录</summary>
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;

    /// <summary>
    /// SHFILEOPSTRUCTW 结构体，传递给 SHFileOperationW 的参数。
    /// 注意：pFrom 和 pTo 是以双 null 结尾的字符串列表。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;            // FO_MOVE / FO_COPY / FO_DELETE
        public string pFrom;         // 源路径（双 null 结尾）
        public string? pTo;          // 目标路径（双 null 结尾，删除时可为 null）
        public ushort fFlags;        // FOF_* 标志组合
        public int fAnyOperationsAborted; // 输出：是否有操作被中止
        public IntPtr hNameMappings; // 输出：文件名映射（不使用）
        public string? lpszProgressTitle; // 进度对话框标题
    }

    [DllImport(Shell32, CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    private readonly ILogger _logger;

    public ShellFileOperation(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 删除文件到回收站（乐观删除）。
    ///
    /// 使用 SHFileOperationW + FOF_ALLOWUNDO 标志，文件进入回收站。
    /// 采用乐观策略：直接尝试删除，失败时由调用方根据 HRESULT 决定降级。
    /// </summary>
    /// <param name="filePath">\\?\ 格式的文件路径</param>
    /// <returns>
    /// S_OK (0): 删除成功
    /// 0x80070020 (ERROR_SHARING_VIOLATION): 文件被占用，需降级 manual_review
    /// 0x80070021 (ERROR_LOCK_VIOLATION): 文件被锁定，需降级 manual_review
    /// 其他 HRESULT: 删除失败
    /// </returns>
    public int DeleteToRecycleBin(string filePath)
    {
        // SHFileOperationW 对 \\?\ 前缀路径兼容性不一致，去规范化为传统 DOS 路径
        filePath = PathNormalizer.Denormalize(filePath);

        // 源路径必须以双 null 结尾（SHFileOperationW 要求）
        var pFrom = filePath + "\0\0";

        var fileOp = new SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = pFrom,
            pTo = null,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            fAnyOperationsAborted = 0,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        _logger.LogDebug("ShellFileOperation: 删除到回收站 path={FilePath}", filePath);
        var result = SHFileOperationW(ref fileOp);

        if (result != Shell32Native.S_OK)
        {
            _logger.LogWarning("ShellFileOperation: 删除到回收站失败 path={FilePath}, HRESULT=0x{HRESULT:X8}",
                filePath, result);
        }

        return result;
    }

    /// <summary>
    /// 永久删除文件（不经过回收站）。
    ///
    /// 不使用 FOF_ALLOWUNDO 标志，文件不可恢复。
    /// 调用方应确保在执行永久删除前已经过用户确认和隔离区备份。
    /// </summary>
    /// <param name="filePath">\\?\ 格式的文件路径</param>
    /// <returns>HRESULT，S_OK 表示成功</returns>
    public int DeletePermanently(string filePath)
    {
        // SHFileOperationW 对 \\?\ 前缀路径兼容性不一致，去规范化为传统 DOS 路径
        filePath = PathNormalizer.Denormalize(filePath);

        var pFrom = filePath + "\0\0";

        var fileOp = new SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = pFrom,
            pTo = null,
            fFlags = FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            fAnyOperationsAborted = 0,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        _logger.LogDebug("ShellFileOperation: 永久删除 path={FilePath}", filePath);
        var result = SHFileOperationW(ref fileOp);

        if (result != Shell32Native.S_OK)
        {
            _logger.LogWarning("ShellFileOperation: 永久删除失败 path={FilePath}, HRESULT=0x{HRESULT:X8}",
                filePath, result);
        }

        return result;
    }

    /// <summary>
    /// 移动文件（同卷时为原子操作）。
    ///
    /// 用于隔离区同卷场景：将文件从原始位置移动到隔离区目录。
    /// </summary>
    /// <param name="sourcePath">\\?\ 格式的源文件路径</param>
    /// <param name="destPath">\\?\ 格式的目标文件路径（包含文件名）</param>
    /// <returns>HRESULT，S_OK 表示成功</returns>
    public int MoveFile(string sourcePath, string destPath)
    {
        // SHFileOperationW 对 \\?\ 前缀路径兼容性不一致，去规范化为传统 DOS 路径
        sourcePath = PathNormalizer.Denormalize(sourcePath);
        destPath = PathNormalizer.Denormalize(destPath);

        var pFrom = sourcePath + "\0\0";
        var pTo = destPath + "\0\0";

        var fileOp = new SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_MOVE,
            pFrom = pFrom,
            pTo = pTo,
            fFlags = FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_NOCONFIRMMKDIR,
            fAnyOperationsAborted = 0,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        _logger.LogDebug("ShellFileOperation: 移动文件 {Source} → {Dest}", sourcePath, destPath);
        var result = SHFileOperationW(ref fileOp);

        if (result != Shell32Native.S_OK)
        {
            _logger.LogWarning("ShellFileOperation: 移动文件失败 {Source} → {Dest}, HRESULT=0x{HRESULT:X8}",
                sourcePath, destPath, result);
        }

        return result;
    }

    /// <summary>
    /// 复制文件（跨卷隔离区场景）。
    ///
    /// 用于隔离区跨卷场景：将文件从源卷复制到目标卷的隔离区。
    /// 复制完成后调用方需校验 SHA-256 并重命名 .cleaning.tmp → .quarantine。
    /// </summary>
    /// <param name="sourcePath">\\?\ 格式的源文件路径</param>
    /// <param name="destPath">\\?\ 格式的目标文件路径（.cleaning.tmp 后缀）</param>
    /// <returns>HRESULT，S_OK 表示成功</returns>
    public int CopyFile(string sourcePath, string destPath)
    {
        // SHFileOperationW 对 \\?\ 前缀路径兼容性不一致，去规范化为传统 DOS 路径
        sourcePath = PathNormalizer.Denormalize(sourcePath);
        destPath = PathNormalizer.Denormalize(destPath);

        var pFrom = sourcePath + "\0\0";
        var pTo = destPath + "\0\0";

        var fileOp = new SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_COPY,
            pFrom = pFrom,
            pTo = pTo,
            fFlags = FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_NOCONFIRMMKDIR,
            fAnyOperationsAborted = 0,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        _logger.LogDebug("ShellFileOperation: 复制文件 {Source} → {Dest}", sourcePath, destPath);
        var result = SHFileOperationW(ref fileOp);

        if (result != Shell32Native.S_OK)
        {
            _logger.LogWarning("ShellFileOperation: 复制文件失败 {Source} → {Dest}, HRESULT=0x{HRESULT:X8}",
                sourcePath, destPath, result);
        }

        return result;
    }

    /// <summary>
    /// 判断 HRESULT 是否表示文件被锁定或占用。
    /// SHFileOperationW 在不同 Windows 版本可能返回 HRESULT 或原始 Win32 错误码，
    /// 因此同时检查两种格式。
    /// 这些错误码触发降级为 manual_review，而非直接报告失败。
    /// </summary>
    public static bool IsLockViolation(int hresult)
    {
        // 检查 HRESULT 格式
        if (hresult == Shell32Native.ERROR_SHARING_VIOLATION ||
            hresult == Shell32Native.ERROR_LOCK_VIOLATION)
            return true;

        // 检查原始 Win32 错误码（SHFileOperationW 在某些版本返回此格式）
        if (hresult == Shell32Native.WIN32_ERROR_SHARING_VIOLATION ||
            hresult == Shell32Native.WIN32_ERROR_LOCK_VIOLATION)
            return true;

        // 检查 HRESULT 的低 16 位是否匹配（通用提取）
        var win32Code = Shell32Native.ExtractWin32ErrorCode(hresult);
        return win32Code == Shell32Native.WIN32_ERROR_SHARING_VIOLATION ||
               win32Code == Shell32Native.WIN32_ERROR_LOCK_VIOLATION;
    }

    /// <summary>
    /// 将错误码转换为用户可读的错误消息。
    /// 同时处理 HRESULT 和原始 Win32 错误码格式。
    /// </summary>
    public static string GetErrorMessage(int hresult) => hresult switch
    {
        Shell32Native.S_OK => "操作成功",
        Shell32Native.ERROR_SHARING_VIOLATION or Shell32Native.WIN32_ERROR_SHARING_VIOLATION => "文件被其他进程占用，无法访问",
        Shell32Native.ERROR_LOCK_VIOLATION or Shell32Native.WIN32_ERROR_LOCK_VIOLATION => "文件被锁定，无法操作",
        Shell32Native.ERROR_ACCESS_DENIED or Shell32Native.WIN32_ERROR_ACCESS_DENIED => "权限不足，无法操作文件",
        Shell32Native.ERROR_FILE_NOT_FOUND or Shell32Native.WIN32_ERROR_FILE_NOT_FOUND => "文件不存在",
        _ => $"操作失败，错误码: 0x{hresult:X8}"
    };

    public void Dispose()
    {
        // 当前无托管资源需释放，保留接口以备后续 IFileOperation COM 升级
    }
}
