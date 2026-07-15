using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace DirectoryCleanAgent.Everything.Interop;

/// <summary>
/// Everything SDK 原生 API 的 P/Invoke 封装。
///
/// 所有 Everything SDK 函数通过 Everything64.dll 调用（64位进程）。
/// 关键版本要求：Everything ≥ 1.4.1.1000（该版本起稳定支持 FRN）。
///
/// 版本号编码规则（参考 Everything SDK 头文件）：
///   - Bits 31-24: 主版本号 (Major)
///   - Bits 23-16: 次版本号 (Minor)
///   - Bits 15-8:  修订号 (Revision)
///   - Bits 7-0:   构建号 (Build, 最大 255)
/// 由于构建号仅 8 位，1.4.1.1000 中的 1000 实为 Everything 应用程序的完整构建号，
/// SDK DLL 的构建号字段可能不同。因此版本校验优先比较 Major/Minor/Revision，
/// 构建号作为辅助参考。
/// </summary>
internal static partial class EverythingNative
{
    private const string DllName = "Everything64.dll";

    /// <summary>
    /// Everything.exe 所在目录（由 EverythingDependencyDetector 在进程检测时填入）。
    /// 用于便携版/Lite 版场景下从进程路径旁加载 SDK DLL。
    /// </summary>
    internal static string? EverythingProcessDirectory { get; set; }

    static EverythingNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(EverythingNative).Assembly, ResolveEverythingDll);
    }

    private static IntPtr ResolveEverythingDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != DllName)
            return IntPtr.Zero;

        // 1. 检查 Everything.exe 进程所在目录（便携版/Lite 版兼容）
        if (EverythingProcessDirectory is { } dir)
        {
            var dllPath = Path.Combine(dir, DllName);
            if (File.Exists(dllPath))
            {
                return NativeLibrary.Load(dllPath);
            }
        }

        // 2. 检查应用程序输出目录（开发构建时 DLL 被复制到此处）
        var appDirDll = Path.Combine(AppContext.BaseDirectory, DllName);
        if (File.Exists(appDirDll))
        {
            return NativeLibrary.Load(appDirDll);
        }

        // 3. 检查捆绑安装的 Everything 便携版目录
        var portableDll = Path.Combine(AppContext.BaseDirectory, "Everything", DllName);
        if (File.Exists(portableDll))
        {
            return NativeLibrary.Load(portableDll);
        }

        // 4. 未找到，返回 0 让系统默认搜索路径处理
        return IntPtr.Zero;
    }

    // ================================================================
    // 版本与状态
    // ================================================================

    // 注意：Everything64.dll 不导出 Everything_GetVersion 统一版本函数。
    // SDK 版本号通过独立分量函数获取：Everything_GetMajorVersion + Everything_GetMinorVersion
    // + Everything_GetRevision + Everything_GetBuildNumber。
    // 统一版本号计算: RawVersion = (major << 24) | (minor << 16) | (rev << 8) | build。
    //
    // 以下声明使用 Everything SDK 的实际导出函数（参见 lib/Everything-SDK/src/Everything64.def）。

    /// <summary>获取 Everything 主版本号</summary>
    [DllImport(DllName)]
    internal static extern uint Everything_GetMajorVersion();

    /// <summary>获取 Everything 次版本号</summary>
    [DllImport(DllName)]
    internal static extern uint Everything_GetMinorVersion();

    /// <summary>获取 Everything 修订号</summary>
    [DllImport(DllName)]
    internal static extern uint Everything_GetRevision();

    /// <summary>获取 Everything 构建号</summary>
    [DllImport(DllName)]
    internal static extern uint Everything_GetBuildNumber();

    /// <summary>
    /// 检查 Everything 数据库是否已加载完毕。
    /// 数据库未就绪时，查询可能返回不完整结果。
    /// </summary>
    [DllImport(DllName)]
    internal static extern bool Everything_IsDBLoaded();

    /// <summary>
    /// 获取最后一次 SDK 调用的错误码。
    /// 返回 0 (EVERYTHING_OK) 表示无错误。
    /// </summary>
    [DllImport(DllName)]
    internal static extern uint Everything_GetLastError();

    // ================================================================
    // 搜索与查询
    // ================================================================

    /// <summary>
    /// 设置 Everything 搜索表达式。
    /// 支持通配符、布尔运算符、宏（如 ext:log、size:>100mb）。
    /// 空字符串表示匹配所有文件。
    /// </summary>
    [DllImport(DllName, CharSet = CharSet.Unicode)]
    internal static extern void Everything_SetSearch(string lpSearchString);

    /// <summary>
    /// 执行搜索查询。
    /// </summary>
    /// <param name="wait">true=同步等待查询完成；false=异步返回</param>
    /// <returns>true 表示查询成功执行</returns>
    [DllImport(DllName)]
    internal static extern bool Everything_Query(bool wait);

    /// <summary>
    /// 获取当前查询的结果总数。
    /// 需在 Everything_Query 成功后调用。
    /// </summary>
    [DllImport(DllName)]
    internal static extern uint Everything_GetNumResults();

    // ================================================================
    // 结果获取
    // ================================================================

    /// <summary>
    /// 获取指定索引结果的文件完整路径。
    /// 路径使用 Windows 标准格式（非 \\?\ 格式），调用方需自行转换。
    /// </summary>
    /// <param name="index">结果索引（0-based）</param>
    /// <param name="buf">预分配 StringBuilder 缓冲区</param>
    /// <param name="bufSize">缓冲区字符容量</param>
    [DllImport(DllName, CharSet = CharSet.Unicode)]
    internal static extern void Everything_GetResultFullPathName(
        uint index, StringBuilder buf, uint bufSize);

    /// <summary>
    /// 获取指定索引结果的文件大小（字节）。
    /// 原生签名: BOOL Everything_GetResultSize(DWORD index, LARGE_INTEGER *lpSize)
    /// 目录返回 -1（LARGE_INTEGER.QuadPart = -1）。
    /// </summary>
    [DllImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Everything_GetResultSize(uint index, out long lpSize);

    /// <summary>
    /// 获取指定索引结果的文件引用号（File Reference Number, FRN）。
    /// 原生签名: BOOL Everything_GetResultFileReferenceNumber(DWORD index, LARGE_INTEGER *lpFRN)
    /// FRN 是 NTFS 文件系统上的物理文件标识，与 VolumeGuid 组合可唯一标识文件。
    /// 返回 FALSE 或 lpFRN=0 表示不可用（非 NTFS 卷或 Everything 版本过低）。
    /// </summary>
    [DllImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Everything_GetResultFileReferenceNumber(uint index, out long lpFileReferenceNumber);

    // ================================================================
    // 排序参数（用于后续 B1 阶段流式枚举）
    // ================================================================

    [DllImport(DllName)]
    internal static extern void Everything_SetSort(uint sortType);

    [DllImport(DllName)]
    internal static extern uint Everything_GetSort();

    /// <summary>按名称升序</summary>
    internal const uint SORT_NAME_ASCENDING = 1;
    /// <summary>按名称降序</summary>
    internal const uint SORT_NAME_DESCENDING = 2;
    /// <summary>按路径升序</summary>
    internal const uint SORT_PATH_ASCENDING = 3;
    /// <summary>按路径降序</summary>
    internal const uint SORT_PATH_DESCENDING = 4;
    /// <summary>按大小升序</summary>
    internal const uint SORT_SIZE_ASCENDING = 5;
    /// <summary>按大小降序</summary>
    internal const uint SORT_SIZE_DESCENDING = 6;
    /// <summary>按扩展名升序</summary>
    internal const uint SORT_EXTENSION_ASCENDING = 7;
    /// <summary>按扩展名降序</summary>
    internal const uint SORT_EXTENSION_DESCENDING = 8;
    /// <summary>按修改日期升序</summary>
    internal const uint SORT_DATE_MODIFIED_ASCENDING = 11;
    /// <summary>按修改日期降序</summary>
    internal const uint SORT_DATE_MODIFIED_DESCENDING = 12;

    // ================================================================
    // 扩展结果字段（B1 流式适配器使用）
    // ================================================================

    /// <summary>
    /// 获取指定索引结果的文件修改日期。
    /// 原生签名: BOOL Everything_GetResultDateModified(DWORD index, LARGE_INTEGER *lpDateModified)
    /// 返回 Win32 FILETIME（自 1601-01-01 UTC 起的 100 纳秒间隔数）。
    /// 使用 DateTime.FromFileTimeUtc() 转换为 DateTime。
    /// 返回 FALSE 或 lpDateModified=0 表示该文件元数据不可用。
    /// </summary>
    [DllImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Everything_GetResultDateModified(uint index, out long lpDateModified);

    /// <summary>
    /// 获取指定索引结果所在的 NTFS 卷序列号（Volume Serial Number）。
    /// 原生签名: BOOL Everything_GetResultVolumeSerial(DWORD index, DWORD *lpVolumeSerial)
    /// 返回 32 位卷序列号，格式化为 "XXXX-XXXX" 形式用作 VolumeGuid。
    /// 返回 FALSE 或 lpVolumeSerial=0 表示卷信息不可用。
    /// </summary>
    [DllImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Everything_GetResultVolumeSerial(uint index, out uint lpVolumeSerial);

    /// <summary>
    /// 设置 Everything 请求字段标志位。
    /// 通过按位或组合需要获取的字段，减少不必要的数据传输。
    /// 应在 Everything_SetSearch 之后、Everything_Query 之前调用。
    /// </summary>
    [DllImport(DllName)]
    internal static extern void Everything_SetRequestFlags(uint flags);

    /// <summary>
    /// 获取当前 Everything 请求字段标志位。
    /// </summary>
    [DllImport(DllName)]
    internal static extern uint Everything_GetRequestFlags();

    /// <summary>
    /// 设置返回的最大结果数。
    /// 0 表示无限制（返回全部匹配结果）。
    /// </summary>
    [DllImport(DllName)]
    internal static extern void Everything_SetMax(uint max);

    /// <summary>
    /// 设置结果偏移量（0-based），用于后续查询跳过前 N 条结果。
    /// </summary>
    [DllImport(DllName)]
    internal static extern void Everything_SetOffset(uint offset);

    // ================================================================
    // 请求字段标志常量（按位或组合）
    // 参考 Everything SDK 头文件 Everything.h
    // ================================================================

    /// <summary>请求文件名（不含路径）</summary>
    internal const uint REQUEST_FILE_NAME = 0x00000001;

    /// <summary>请求完整路径</summary>
    internal const uint REQUEST_PATH = 0x00000002;

    /// <summary>请求完整路径和文件名</summary>
    internal const uint REQUEST_FULL_PATH_AND_FILE_NAME = 0x00000004;

    /// <summary>请求扩展名</summary>
    internal const uint REQUEST_EXTENSION = 0x00000008;

    /// <summary>请求文件大小（字节）</summary>
    internal const uint REQUEST_SIZE = 0x00000010;

    /// <summary>请求最后修改日期（FILETIME）</summary>
    internal const uint REQUEST_DATE_MODIFIED = 0x00000040;

    /// <summary>请求文件引用号（FRN）</summary>
    internal const uint REQUEST_FILE_REFERENCE_NUMBER = 0x00010000;

    /// <summary>请求卷序列号</summary>
    internal const uint REQUEST_VOLUME_SERIAL = 0x00040000;

    /// <summary>请求 B1 适配器所需的全部字段</summary>
    internal const uint REQUEST_ALL =
        REQUEST_FILE_NAME |
        REQUEST_PATH |
        REQUEST_SIZE |
        REQUEST_DATE_MODIFIED |
        REQUEST_FILE_REFERENCE_NUMBER |
        REQUEST_VOLUME_SERIAL;

    // ================================================================
    // 资源清理
    // ================================================================

    /// <summary>
    /// 释放 Everything SDK 内部资源。
    /// 在应用程序退出或不再需要 Everything 查询时调用。
    /// </summary>
    [DllImport(DllName)]
    internal static extern void Everything_CleanUp();

    // ================================================================
    // 错误码常量
    // ================================================================

    /// <summary>操作成功</summary>
    internal const uint ERROR_OK = 0;
    /// <summary>Everything 未运行时尝试 IPC 通信</summary>
    internal const uint ERROR_IPC = 8;
    /// <summary>Everything 索引未就绪</summary>
    internal const uint ERROR_NOT_READY = 9;
}
