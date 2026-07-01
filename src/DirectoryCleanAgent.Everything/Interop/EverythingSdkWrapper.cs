using System.Text;

namespace DirectoryCleanAgent.Everything.Interop;

/// <summary>
/// IEverythingSdk 的生产实现 — 基于 Everything SDK 2.0（Everything ≥ 1.4.1）。
///
/// SDK 2.0 使用全局状态模型：所有搜索参数（搜索表达式、排序、请求字段等）是进程全局的，
/// 同一时刻只能有一个操作访问 SDK。线程安全由调用方（EverythingFileListProvider）
/// 通过 SemaphoreSlim 保证。
///
/// 与 SDK 3.0 的关键差异：
///   - 无需 Connect/Disconnect（SDK 2.0 通过 Everything 进程 IPC 自动通信）
///   - 无客户端句柄，所有调用为静态全局方法
///   - 结果访问使用 uint 索引而非结果列表句柄 + UIntPtr 索引
/// </summary>
internal sealed class EverythingSdkWrapper : IEverythingSdk
{
    private bool _disposed;

    // SDK 2.0 无连接概念，IsConnected 在未释放时始终为 true
    public bool IsConnected => !_disposed;

    // ================================================================
    // 生命周期
    // ================================================================

    /// <inheritdoc/>
    public void Connect(string? instanceName = null)
    {
        ThrowIfDisposed();
        // SDK 2.0 无需显式连接，IPC 通信在首次查询时自动建立
    }

    /// <inheritdoc/>
    public void Disconnect()
    {
        // SDK 2.0 无显式断开操作，资源在 CleanUp/Dispose 时释放
    }

    // ================================================================
    // 版本与状态
    // ================================================================

    /// <inheritdoc/>
    public uint GetMajorVersion()
    {
        ThrowIfDisposed();
        return EverythingNative.Everything_GetMajorVersion();
    }

    /// <inheritdoc/>
    public uint GetMinorVersion()
    {
        ThrowIfDisposed();
        return EverythingNative.Everything_GetMinorVersion();
    }

    /// <inheritdoc/>
    public uint GetRevision()
    {
        ThrowIfDisposed();
        return EverythingNative.Everything_GetRevision();
    }

    /// <inheritdoc/>
    public uint GetBuildNumber()
    {
        ThrowIfDisposed();
        return EverythingNative.Everything_GetBuildNumber();
    }

    /// <inheritdoc/>
    public bool IsDBLoaded()
    {
        ThrowIfDisposed();
        return EverythingNative.Everything_IsDBLoaded();
    }

    /// <inheritdoc/>
    public uint GetLastError()
    {
        if (_disposed) return EverythingNative.ERROR_IPC;
        return EverythingNative.Everything_GetLastError();
    }

    // ================================================================
    // 搜索与查询
    // ================================================================

    /// <inheritdoc/>
    public void SetSearch(string search)
    {
        ThrowIfDisposed();
        EverythingNative.Everything_SetSearch(search);
    }

    /// <inheritdoc/>
    public bool Query(bool wait)
    {
        ThrowIfDisposed();
        return EverythingNative.Everything_Query(wait);
    }

    /// <inheritdoc/>
    public uint GetNumResults()
    {
        ThrowIfDisposed();
        return EverythingNative.Everything_GetNumResults();
    }

    // ================================================================
    // 结果获取
    // ================================================================

    /// <inheritdoc/>
    public void GetResultFullPathName(uint index, StringBuilder buf, uint bufSize)
    {
        ThrowIfDisposed();
        EverythingNative.Everything_GetResultFullPathName(index, buf, bufSize);
    }

    /// <inheritdoc/>
    public long GetResultSize(uint index)
    {
        ThrowIfDisposed();
        EverythingNative.Everything_GetResultSize(index, out long size);
        return size;
    }

    /// <inheritdoc/>
    public long GetResultDateModified(uint index)
    {
        ThrowIfDisposed();
        EverythingNative.Everything_GetResultDateModified(index, out long dateModified);
        return dateModified;
    }

    /// <inheritdoc/>
    public long GetResultFileReferenceNumber(uint index)
    {
        ThrowIfDisposed();
        try
        {
            EverythingNative.Everything_GetResultFileReferenceNumber(index, out long frn);
            return frn;
        }
        catch (EntryPointNotFoundException)
        {
            // SDK DLL 版本过旧，不支持 FRN 字段
            return 0;
        }
    }

    /// <inheritdoc/>
    public uint GetResultVolumeSerial(uint index)
    {
        ThrowIfDisposed();
        try
        {
            EverythingNative.Everything_GetResultVolumeSerial(index, out uint volumeSerial);
            return volumeSerial;
        }
        catch (EntryPointNotFoundException)
        {
            // SDK DLL 版本过旧，不支持卷序列号字段
            return 0;
        }
    }

    // ================================================================
    // 查询参数
    // ================================================================

    /// <inheritdoc/>
    public void SetSort(uint sortType)
    {
        ThrowIfDisposed();
        EverythingNative.Everything_SetSort(sortType);
    }

    /// <inheritdoc/>
    public void SetRequestFlags(uint flags)
    {
        ThrowIfDisposed();
        EverythingNative.Everything_SetRequestFlags(flags);
    }

    // ================================================================
    // IDisposable
    // ================================================================

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            EverythingNative.Everything_CleanUp();
        }
        catch
        {
            // 资源清理失败不应影响正常退出流程
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(EverythingSdkWrapper));
    }
}
