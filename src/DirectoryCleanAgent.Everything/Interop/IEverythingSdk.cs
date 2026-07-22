using System.Text;

namespace DirectoryCleanAgent.Everything.Interop;

/// <summary>
/// Everything SDK 的可测试抽象接口（适配 SDK 3.0 / Everything ≥ 1.5）。
///
/// 封装基于客户端句柄的 Everything3 API，为上层提供简化的搜索抽象。
/// 生产环境使用 Everything3SdkWrapper，测试环境使用 Mock&lt;IEverythingSdk&gt;。
///
/// 生命周期：
///   1. Connect(instanceName) — 连接 Everything IPC 管道服务
///   2. SetSearch / SetSort / SetRequestFlags / Query / GetResult* — 执行搜索并获取结果
///   3. Disconnect() 或 Dispose() — 释放客户端和所有 SDK 资源
/// </summary>
public interface IEverythingSdk : IDisposable
{
    // ================================================================
    // 生命周期
    // ================================================================

    /// <summary>
    /// 连接到 Everything 命名管道 IPC 服务。
    /// 必须在使用任何搜索功能之前调用。
    /// </summary>
    /// <param name="instanceName">
    /// Everything 实例名称。null 或空字符串表示默认实例；
    /// "1.5a" 表示 Everything 1.5 alpha 实例。
    /// </param>
    void Connect(string? instanceName = null);

    /// <summary>断开连接并释放客户端资源</summary>
    void Disconnect();

    /// <summary>客户端是否已连接</summary>
    bool IsConnected { get; }

    // ================================================================
    // 版本与状态
    // ================================================================

    /// <summary>获取 Everything 主版本号</summary>
    uint GetMajorVersion();

    /// <summary>获取 Everything 次版本号</summary>
    uint GetMinorVersion();

    /// <summary>获取 Everything 修订号</summary>
    uint GetRevision();

    /// <summary>获取 Everything 构建号</summary>
    uint GetBuildNumber();

    /// <summary>检查 Everything 数据库是否已加载</summary>
    bool IsDBLoaded();

    /// <summary>获取最后一次 SDK 调用的错误码</summary>
    uint GetLastError();

    // ================================================================
    // 搜索与查询
    // ================================================================

    /// <summary>设置 Everything 搜索表达式</summary>
    void SetSearch(string search);

    /// <summary>执行查询，wait=true 同步等待完成</summary>
    bool Query(bool wait);

    /// <summary>获取当前查询的结果总数</summary>
    uint GetNumResults();

    // ================================================================
    // 结果获取
    // ================================================================

    /// <summary>获取指定索引结果的文件完整路径</summary>
    void GetResultFullPathName(uint index, StringBuilder buf, uint bufSize);

    /// <summary>获取指定索引结果的文件大小（字节），目录返回 -1</summary>
    long GetResultSize(uint index);

    /// <summary>
    /// 判断指定索引结果是否为文件夹。
    /// 目录判断的权威依据——开启"索引文件夹大小"后目录 Size 可能 ≥ 0。
    /// </summary>
    bool IsFolderResult(uint index);

    /// <summary>获取指定索引结果的文件修改日期（FILETIME）</summary>
    long GetResultDateModified(uint index);

    /// <summary>获取指定索引结果的文件引用号（FRN）</summary>
    long GetResultFileReferenceNumber(uint index);

    /// <summary>获取指定索引结果的 NTFS 卷序列号</summary>
    uint GetResultVolumeSerial(uint index);

    // ================================================================
    // 查询参数
    // ================================================================

    /// <summary>设置 Everything 原生排序类型</summary>
    void SetSort(uint sortType);

    /// <summary>设置请求字段标志位（按位或组合）</summary>
    void SetRequestFlags(uint flags);

    // ================================================================
    // 结果限制
    // ================================================================

    /// <summary>获取当前设置的最大结果数（0 表示无限制）</summary>
    uint GetMax();

    /// <summary>设置最大结果数（0 表示无限制）</summary>
    void SetMax(uint max);

    /// <summary>重置为无限制（等价于 SetMax(0)）</summary>
    void ResetMax() => SetMax(0);
}
