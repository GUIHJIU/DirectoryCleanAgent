using System.Runtime.InteropServices;
using System.Text;

namespace DirectoryCleanAgent.Everything.Interop;

/// <summary>
/// Everything SDK 3.0 原生 API 的 P/Invoke 封装（Everything ≥ 1.5）。
///
/// Everything 1.5 引入了 SDK 3.0，API 从旧有的全局状态模型迁移为基于
/// 客户端句柄（EVERYTHING3_CLIENT*）和命名管道的架构。
///
/// 关键变更（相比 SDK v2 / Everything 1.4）：
///   - 函数前缀从 Everything_ 变更为 Everything3_
///   - 通信方式从 WM_COPYDATA 变更为命名管道（\\.\PIPE\Everything IPC）
///   - 所有操作需要先通过 Everything3_ConnectW 获取客户端句柄
///   - 搜索/查询使用状态对象模型（SearchState → Search → ResultList）
///
/// 向后兼容性注意事项：
///   SDK 3.0 与 v2 不兼容。若目标环境可能安装 Everything 1.4，
///   需在 DetectAsync 中探测 DLL 导出，回退到旧版 EverythingNative API。
/// </summary>
internal static partial class Everything3Native
{
    private const string DllName = "Everything64.dll";

    // ================================================================
    // 连接管理
    // ================================================================

    /// <summary>
    /// 连接到 Everything 命名管道 IPC 服务。
    /// </summary>
    /// <param name="instanceName">
    /// Everything 实例名称。
    /// - null 或空字符串 → 默认实例
    /// - "1.5a" → Everything 1.5 alpha 实例
    /// </param>
    /// <returns>客户端句柄（EVERYTHING3_CLIENT*），失败时返回 IntPtr.Zero</returns>
    [DllImport(DllName, CharSet = CharSet.Unicode)]
    internal static extern IntPtr Everything3_ConnectW(
        [MarshalAs(UnmanagedType.LPWStr)] string? instanceName);

    /// <summary>
    /// 断开与 Everything IPC 服务的连接并释放客户端资源。
    /// </summary>
    [DllImport(DllName)]
    internal static extern void Everything3_DestroyClient(IntPtr client);

    /// <summary>
    /// 取消所有待处理/活动请求并标记客户端为关闭状态。
    /// 后续操作将返回 EVERYTHING3_ERROR_SHUTDOWN (0xE000000C)。
    /// </summary>
    [DllImport(DllName)]
    internal static extern void Everything3_ShutdownClient(IntPtr client);

    // ================================================================
    // 版本查询（需要已连接的客户端）
    // ================================================================

    /// <summary>获取 Everything 主版本号（如 1）</summary>
    [DllImport(DllName)]
    internal static extern uint Everything3_GetMajorVersion(IntPtr client);

    /// <summary>获取 Everything 次版本号（如 5）</summary>
    [DllImport(DllName)]
    internal static extern uint Everything3_GetMinorVersion(IntPtr client);

    /// <summary>获取 Everything 修订号</summary>
    [DllImport(DllName)]
    internal static extern uint Everything3_GetRevision(IntPtr client);

    /// <summary>获取 Everything 构建号</summary>
    [DllImport(DllName)]
    internal static extern uint Everything3_GetBuildNumber(IntPtr client);

    /// <summary>获取 Everything 目标机器架构</summary>
    [DllImport(DllName)]
    internal static extern uint Everything3_GetTargetMachine(IntPtr client);

    /// <summary>获取 IPC 管道协议版本</summary>
    [DllImport(DllName)]
    internal static extern uint Everything3_GetIPCPipeVersion(IntPtr client);

    // ================================================================
    // 状态查询
    // ================================================================

    /// <summary>检查 Everything 数据库是否已加载</summary>
    [DllImport(DllName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Everything3_IsDBLoaded(IntPtr client);

    /// <summary>获取最后一次 SDK 调用的错误码</summary>
    [DllImport(DllName)]
    internal static extern uint Everything3_GetLastError(IntPtr client);

    // ================================================================
    // 搜索状态管理
    // ================================================================

    /// <summary>创建搜索状态对象</summary>
    /// <returns>EVERYTHING3_SEARCH_STATE*，失败时返回 IntPtr.Zero</returns>
    [DllImport(DllName)]
    internal static extern IntPtr Everything3_CreateSearchState();

    /// <summary>销毁搜索状态对象并释放资源</summary>
    [DllImport(DllName)]
    internal static extern void Everything3_DestroySearchState(IntPtr searchState);

    /// <summary>设置搜索文本表达式</summary>
    [DllImport(DllName, CharSet = CharSet.Unicode)]
    internal static extern void Everything3_SetSearchTextW(
        IntPtr searchState,
        [MarshalAs(UnmanagedType.LPWStr)] string text);

    /// <summary>设置搜索结果的最大数量（0 = 无限制）</summary>
    [DllImport(DllName)]
    internal static extern void Everything3_SetMax(IntPtr searchState, uint max);

    /// <summary>设置结果偏移量（用于分页）</summary>
    [DllImport(DllName)]
    internal static extern void Everything3_SetOffset(IntPtr searchState, uint offset);

    /// <summary>设置是否区分大小写</summary>
    [DllImport(DllName)]
    internal static extern void Everything3_SetMatchCase(IntPtr searchState, [MarshalAs(UnmanagedType.Bool)] bool matchCase);

    /// <summary>设置是否全词匹配</summary>
    [DllImport(DllName)]
    internal static extern void Everything3_SetMatchWholeWord(IntPtr searchState, [MarshalAs(UnmanagedType.Bool)] bool matchWholeWord);

    /// <summary>设置是否匹配路径</summary>
    [DllImport(DllName)]
    internal static extern void Everything3_SetMatchPath(IntPtr searchState, [MarshalAs(UnmanagedType.Bool)] bool matchPath);

    /// <summary>设置排序类型</summary>
    [DllImport(DllName)]
    internal static extern void Everything3_SetSort(IntPtr searchState, uint sortType);

    /// <summary>设置请求字段标志位</summary>
    [DllImport(DllName)]
    internal static extern void Everything3_SetRequestFlags(IntPtr searchState, uint flags);

    // ================================================================
    // 执行搜索
    // ================================================================

    /// <summary>
    /// 执行搜索查询。
    /// </summary>
    /// <param name="client">已连接的客户端句柄</param>
    /// <param name="searchState">搜索状态对象（包含搜索表达式、排序、字段请求等参数）</param>
    /// <returns>EVERYTHING3_RESULT_LIST*，失败时返回 IntPtr.Zero</returns>
    [DllImport(DllName)]
    internal static extern IntPtr Everything3_Search(IntPtr client, IntPtr searchState);

    // ================================================================
    // 结果列表管理
    // ================================================================

    /// <summary>销毁结果列表并释放资源</summary>
    [DllImport(DllName)]
    internal static extern void Everything3_DestroyResultList(IntPtr resultList);

    /// <summary>获取结果列表中的结果数量</summary>
    [DllImport(DllName)]
    internal static extern UIntPtr Everything3_GetResultListViewportCount(IntPtr resultList);

    /// <summary>获取指定索引结果的文件完整路径</summary>
    [DllImport(DllName, CharSet = CharSet.Unicode)]
    internal static extern void Everything3_GetResultFullPathNameW(
        IntPtr resultList,
        UIntPtr index,
        StringBuilder lpString,
        uint nMaxCount);

    /// <summary>获取指定索引结果的文件名（不含路径）</summary>
    [DllImport(DllName, CharSet = CharSet.Unicode)]
    internal static extern void Everything3_GetResultFileNameW(
        IntPtr resultList,
        UIntPtr index,
        StringBuilder lpString,
        uint nMaxCount);

    /// <summary>获取指定索引结果的文件路径（不含文件名）</summary>
    [DllImport(DllName, CharSet = CharSet.Unicode)]
    internal static extern void Everything3_GetResultPathW(
        IntPtr resultList,
        UIntPtr index,
        StringBuilder lpString,
        uint nMaxCount);

    // ================================================================
    // 结果字段访问器
    // ================================================================

    /// <summary>获取指定索引结果的文件大小（字节），目录返回 -1</summary>
    [DllImport(DllName)]
    internal static extern long Everything3_GetResultSize(IntPtr resultList, UIntPtr index);

    /// <summary>获取指定索引结果的文件修改日期（Win32 FILETIME）</summary>
    [DllImport(DllName)]
    internal static extern long Everything3_GetResultDateModified(IntPtr resultList, UIntPtr index);

    /// <summary>获取指定索引结果的文件引用号（FRN），非 NTFS 卷返回 0</summary>
    [DllImport(DllName)]
    internal static extern long Everything3_GetResultFileReferenceNumber(IntPtr resultList, UIntPtr index);

    /// <summary>获取指定索引结果的 NTFS 卷序列号</summary>
    [DllImport(DllName)]
    internal static extern uint Everything3_GetResultVolumeSerial(IntPtr resultList, UIntPtr index);

    // ================================================================
    // 资源清理
    // ================================================================

    /// <summary>释放 Everything SDK 分配的全部线程局部资源</summary>
    [DllImport(DllName)]
    internal static extern void Everything3_CleanUp();

    // ================================================================
    // 排序常量（Everything 内部排序类型 ID，跨 SDK 版本不变）
    // ================================================================

    internal const uint SORT_NAME_ASCENDING = 1;
    internal const uint SORT_NAME_DESCENDING = 2;
    internal const uint SORT_PATH_ASCENDING = 3;
    internal const uint SORT_PATH_DESCENDING = 4;
    internal const uint SORT_SIZE_ASCENDING = 5;
    internal const uint SORT_SIZE_DESCENDING = 6;
    internal const uint SORT_EXTENSION_ASCENDING = 7;
    internal const uint SORT_EXTENSION_DESCENDING = 8;
    internal const uint SORT_DATE_MODIFIED_ASCENDING = 11;
    internal const uint SORT_DATE_MODIFIED_DESCENDING = 12;

    // ================================================================
    // 请求字段标志常量（Everything 内部字段 ID，跨 SDK 版本不变）
    // ================================================================

    internal const uint REQUEST_FILE_NAME = 0x00000001;
    internal const uint REQUEST_PATH = 0x00000002;
    internal const uint REQUEST_FULL_PATH_AND_FILE_NAME = 0x00000004;
    internal const uint REQUEST_EXTENSION = 0x00000008;
    internal const uint REQUEST_SIZE = 0x00000010;
    internal const uint REQUEST_DATE_CREATED = 0x00000020;
    internal const uint REQUEST_DATE_MODIFIED = 0x00000040;
    internal const uint REQUEST_DATE_ACCESSED = 0x00000080;
    internal const uint REQUEST_ATTRIBUTES = 0x00000100;
    internal const uint REQUEST_FILE_LIST_FILE_NAME = 0x00000200;
    internal const uint REQUEST_RUN_COUNT = 0x00000400;
    internal const uint REQUEST_DATE_RUN = 0x00000800;
    internal const uint REQUEST_DATE_RECENTLY_CHANGED = 0x00001000;
    internal const uint REQUEST_HIGHLIGHTED_FILE_NAME = 0x00002000;
    internal const uint REQUEST_HIGHLIGHTED_PATH = 0x00004000;
    internal const uint REQUEST_HIGHLIGHTED_FULL_PATH_AND_FILE_NAME = 0x00008000;
    internal const uint REQUEST_FILE_REFERENCE_NUMBER = 0x00010000;
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
    // 错误码常量
    // ================================================================

    /// <summary>操作成功</summary>
    internal const uint ERROR_OK = 0;

    /// <summary>IPC 管道服务未找到（Everything 未运行或实例名不匹配）</summary>
    internal const uint ERROR_IPC_PIPE_NOT_FOUND = 0xE0000002;

    /// <summary>已断开与管道服务的连接</summary>
    internal const uint ERROR_DISCONNECTED = 0xE0000003;

    /// <summary>无效参数</summary>
    internal const uint ERROR_INVALID_PARAMETER = 0xE0000004;

    /// <summary>客户端已被 ShutdownClient 关闭</summary>
    internal const uint ERROR_SHUTDOWN = 0xE000000C;
}
