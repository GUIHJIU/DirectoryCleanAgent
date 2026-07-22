using System.Runtime.CompilerServices;
using System.Text;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Core.PathHandling;
using DirectoryCleanAgent.Everything.Interop;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Everything;

/// <summary>
/// IFileListProvider 的 Everything SDK 实现 — B1 阶段核心交付物。
///
/// 职责：
/// 1. 通过 Everything SDK IPC 查询文件索引，以 IAsyncEnumerable&lt;FileItem&gt; 流式产出
/// 2. 将排序/过滤请求下推至 Everything 原生引擎（不占用托管内存排序）
/// 3. 在 yield return 流中集成墓碑过滤（已删除文件自动跳过）
/// 4. 所有产出路径标准化为 \\?\ 格式
/// 5. 通过定时轮询 Everything 变更实现 FileChanged 事件
///
/// 设计约束：
/// - 内存峰值 &lt;200MB（200 万文件场景），所有枚举使用 yield return
/// - Everything SDK 使用全局进程状态，所有 IPC 调用由 SemaphoreSlim 串行化
/// - 不持有查询结果集 — 结果在锁外逐条产出
/// </summary>
public sealed class EverythingFileListProvider : IFileListProvider, IDisposable
{
    // ================================================================
    // 常量
    // ================================================================

    /// <summary>路径检索缓冲区大小（字符数），支持 \\?\ 长路径</summary>
    private const int PathBufferSize = 4096;

    /// <summary>文件变更轮询间隔（毫秒）</summary>
    private const int ChangePollIntervalMs = 3000;

    /// <summary>Everything dm: 查询的日期时间格式</summary>
    private const string EverythingDateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>单次变更轮询产出的事件数量上限（防止事件风暴）</summary>
    private const int MaxChangeEventsPerPoll = 1000;

    // ================================================================
    // 依赖注入
    // ================================================================

    private readonly ILogger<EverythingFileListProvider> _logger;
    private readonly IConfigService _configService;
    private readonly ITombstoneCache _tombstoneCache;
    private readonly IEverythingSdk _sdk;

    // ================================================================
    // 运行时状态
    // ================================================================

    /// <summary>
    /// 串行化对 Everything SDK 的访问。
    /// Everything SDK 使用全局进程状态（搜索表达式、排序参数等是全局的），
    /// 因此同一时刻只允许一个操作访问 SDK。
    /// </summary>
    private readonly EverythingSdkLock _sdkLock;

    /// <summary>文件变更轮询定时器，在 ThreadPool 线程上触发回调。由 StartChangePolling() 创建。</summary>
    private Timer? _changePollTimer;

    /// <summary>上次轮询的时间戳（UTC），用于构建 Everything dm:&gt; 查询</summary>
    private DateTime _lastPollTimeUtc;

    /// <summary>是否已释放</summary>
    private bool _disposed;

    // ================================================================
    // 事件
    // ================================================================

    /// <inheritdoc/>
    public event EventHandler<FileChangedEventArgs>? FileChanged;

    // ================================================================
    // 构造函数
    // ================================================================

    /// <summary>
    /// 构造 EverythingFileListProvider 实例。
    /// </summary>
    /// <param name="logger">结构化日志记录器</param>
    /// <param name="configService">配置服务（读取 FRN_AVAILABLE 标记）</param>
    /// <param name="tombstoneCache">墓碑内存缓存（启动时已加载）</param>
    /// <param name="sdk">Everything SDK 抽象（生产环境为 EverythingSdkWrapper）</param>
    /// <param name="sdkLock">Everything SDK 全局状态共享锁</param>
    public EverythingFileListProvider(
        ILogger<EverythingFileListProvider> logger,
        IConfigService configService,
        ITombstoneCache tombstoneCache,
        IEverythingSdk sdk,
        EverythingSdkLock sdkLock)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _tombstoneCache = tombstoneCache ?? throw new ArgumentNullException(nameof(tombstoneCache));
        _sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
        _sdkLock = sdkLock ?? throw new ArgumentNullException(nameof(sdkLock));

        _lastPollTimeUtc = DateTime.UtcNow;

        _logger.LogMethodEntry("EverythingFileListProvider 已初始化（变更轮询尚未启动，等待 StartChangePolling() 调用）");
    }

    // ================================================================
    // IFileListProvider.IsIndexingAsync
    // ================================================================

    /// <inheritdoc/>
    /// <remarks>
    /// 委托给 Everything SDK 的 IsDBLoaded 查询。
    /// 返回 true 表示 Everything 正在构建索引（结果不完整）。
    /// IsDBLoaded 是只读操作，不修改 SDK 全局状态，无需获取 _sdkLock。
    /// </remarks>
    public Task<bool> IsIndexingAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            bool isLoaded = _sdk.IsDBLoaded();
            return Task.FromResult(!isLoaded);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查 Everything 索引状态时发生异常");
            return Task.FromResult(true); // 异常时保守假定索引未就绪
        }
    }

    // ================================================================
    // IFileListProvider.EnumerateFilesAsync（核心算法）
    // ================================================================

    /// <inheritdoc/>
    /// <remarks>
    /// 枚举流程（设计文档 4.3 节）：
    /// 1. 获取 SDK 锁 → 设置请求字段标志 → 设置搜索表达式 → 设置排序 → 执行查询 → 获取结果数 → 释放锁
    /// 2. 在锁外逐条遍历结果：
    ///    a. 获取完整路径 → 标准化为 \\?\ 格式
    ///    b. 应用路径过滤 / 卷过滤
    ///    c. 获取文件大小 → 跳过目录（size &lt; 0）
    ///    d. 获取 FRN / 修改日期 / 卷序列号
    ///    e. 构建墓碑键 → 查询墓碑缓存 → 命中则跳过
    ///    f. yield return FileItem
    /// </remarks>
    public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        EverythingQueryParams queryParams,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var searchDesc = queryParams.SearchExpression ?? "(全部)";
        var sortDesc = queryParams.SortType?.ToString() ?? "默认";
        var maxDesc = queryParams.MaxResults?.ToString() ?? "无限制";
        var volDesc = queryParams.Volumes is { Count: > 0 } ? string.Join(",", queryParams.Volumes) : "无限制";
        var pathDesc = queryParams.PathFilter ?? "无";
        _logger.LogMethodEntry(
            $"EnumerateFilesAsync: Search='{searchDesc}', Sort={sortDesc}, " +
            $"Desc={queryParams.SortDescending}, MaxResults={maxDesc}, " +
            $"Volumes={volDesc}, PathFilter={pathDesc}");

        uint resultCount;
        uint mappedSort = EverythingSortMapper.Map(queryParams.SortType, queryParams.SortDescending);

        // ---- 第 1 步：获取 SDK 锁，执行 Everything 查询，并在锁内遍历全部结果 ----
        // SDK 2.0 使用全局进程状态 — 查询结果集是全局的。若在 yield return 期间释放锁，
        // 变更轮询定时器可能执行新查询并覆盖结果集，导致 AccessViolationException。
        // 因此锁必须覆盖整个查询 + 结果访问周期。try/finally 确保枚举器 Dispose 时释放锁。
        await _sdkLock.WaitAsync(ct).ConfigureAwait(false);

        // 读取 FRN 能力标记（由 EverythingDependencyDetector 在启动时写入配置）
        bool frnAvailable = _configService.Current.FRN_AVAILABLE;

        // 可复用的路径缓冲区（减少 GC 压力）
        var pathBuffer = new StringBuilder(PathBufferSize);

        // 统计计数器（用于日志摘要）
        long filesYielded = 0;
        long filesSkippedByTombstone = 0;
        long filesSkippedByFilter = 0;
        long filesSkippedAsDirectory = 0;
        long totalBytesYielded = 0;

        try
        {
            // 清除前序操作可能设置的 Max 限制，确保当前查询返回完整结果集
            _sdk.ResetMax();

            // 设置需要获取的全部字段（减少 Everything 内部不必要的数据传输）
            // FRN/VolumeSerial 仅在检测确认可用时才请求，避免不兼容 SDK 版本触发 AccessViolationException
            uint requestFlags = EverythingNative.REQUEST_FILE_NAME |
                                EverythingNative.REQUEST_PATH |
                                EverythingNative.REQUEST_SIZE |
                                EverythingNative.REQUEST_DATE_MODIFIED;
            if (frnAvailable)
            {
                requestFlags |= EverythingNative.REQUEST_FILE_REFERENCE_NUMBER |
                                EverythingNative.REQUEST_VOLUME_SERIAL;
            }
            _sdk.SetRequestFlags(requestFlags);

            // 构建搜索表达式（含路径过滤下推）。
            // 空 SearchExpression = 匹配全部文件；PathFilter 下推到 Everything 端，
            // 避免返回全系统文件后再在 C# 侧过滤导致 MaxResults 上限被无关条目消耗。
            string searchExpression = BuildSearchExpression(queryParams);
            _sdk.SetSearch(searchExpression);

            // 设置原生排序（0 = Everything 默认排序）
            if (mappedSort != 0)
            {
                _sdk.SetSort(mappedSort);
            }

            // 执行同步查询（wait=true 等待 IPC 返回完整结果）
            if (!_sdk.Query(true))
            {
                uint error = _sdk.GetLastError();

                if (error == EverythingNative.ERROR_IPC)
                {
                    throw new EverythingSdkException(
                        "Everything_Query", error,
                        "Everything IPC 通信失败，Everything 可能未运行。请确认 Everything 已启动并重试。");
                }

                if (error == EverythingNative.ERROR_NOT_READY)
                {
                    throw new EverythingSdkException(
                        "Everything_Query", error,
                        "Everything 索引未就绪，请等待索引构建完成。");
                }

                throw new EverythingSdkException(
                    "Everything_Query", error,
                    $"查询执行失败，错误码: {error}。");
            }

            resultCount = _sdk.GetNumResults();

            // 应用 MaxResults 上限（在托管侧截断，而非通过 Everything_SetMax 下推）。
            // 原因：SDK 2.0 的 Everything_SetMax 是全局持久状态，设置后影响后续所有查询
            // （包括轮询），需要 save/restore 模式配合 IEverythingSdk.GetMax() 接口扩展。
            // TODO(D3): 性能测试后评估是否需要下推优化。
            if (queryParams.MaxResults.HasValue &&
                queryParams.MaxResults.Value < resultCount)
            {
                resultCount = (uint)queryParams.MaxResults.Value;
            }

            _logger.LogInformation("Everything 查询完成: 共 {Count} 条结果", resultCount);

            // ---- 第 2 步：在锁内逐条遍历结果，yield return 产出 FileItem ----
            for (uint i = 0; i < resultCount; i++)
            {
                // 每次迭代检查取消令牌，支持用户随时中断扫描
                ct.ThrowIfCancellationRequested();

                // --- a. 获取完整路径 ---
                pathBuffer.Clear();
                _sdk.GetResultFullPathName(i, pathBuffer, PathBufferSize);
                string rawPath = pathBuffer.ToString();

                if (string.IsNullOrEmpty(rawPath))
                {
                    _logger.LogWarning("Everything 返回空路径，索引: {Index}", i);
                    continue;
                }

                // --- b. 路径标准化为 \\?\ 格式 ---
                string normalizedPath;
                try
                {
                    normalizedPath = PathNormalizer.Normalize(rawPath);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning(ex, "路径标准化失败，索引: {Index}, 路径: {RawPath}", i, rawPath);
                    continue;
                }

                // --- c. 应用 PathFilter 过滤 ---
                if (queryParams.PathFilter is not null &&
                    !normalizedPath.StartsWith(queryParams.PathFilter, StringComparison.OrdinalIgnoreCase))
                {
                    filesSkippedByFilter++;
                    continue;
                }

                // --- d. 应用 Volumes 过滤 ---
                if (queryParams.Volumes is { Count: > 0 })
                {
                    bool volumeMatched = false;
                    foreach (string vol in queryParams.Volumes)
                    {
                        string prefix = @"\\?\" + vol.TrimEnd('\\').TrimEnd(':') + @":\";
                        if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            volumeMatched = true;
                            break;
                        }
                    }

                    if (!volumeMatched)
                    {
                        filesSkippedByFilter++;
                        continue;
                    }
                }

                // --- e. 跳过目录，获取文件大小 ---
                // IsFolderResult 是权威判断：Everything 开启"索引文件夹大小"后
                // 目录 Size ≥ 0（空目录为 0），仅靠 Size 符号无法区分文件与目录；
                // Size < 0 兜底覆盖未开启该选项时的旧行为（目录返回 -1）。
                if (_sdk.IsFolderResult(i))
                {
                    filesSkippedAsDirectory++;
                    continue;
                }

                long sizeBytes = _sdk.GetResultSize(i);

                if (sizeBytes < 0)
                {
                    filesSkippedAsDirectory++;
                    continue;
                }

                // --- f. 获取 FRN、修改日期、卷序列号 ---
                long rawDateModified = _sdk.GetResultDateModified(i);
                long rawFrn = 0;
                uint volumeSerial = 0;
                if (frnAvailable)
                {
                    rawFrn = _sdk.GetResultFileReferenceNumber(i);
                    volumeSerial = _sdk.GetResultVolumeSerial(i);
                }

                DateTime lastWriteTime = rawDateModified != 0
                    ? DateTime.FromFileTimeUtc(rawDateModified)
                    : DateTime.MinValue;

                string? extension = Path.GetExtension(normalizedPath)?.ToLowerInvariant();
                if (string.IsNullOrEmpty(extension))
                {
                    extension = null;
                }

                // --- g. 构建卷标识符 ---
                string? volumeGuid = volumeSerial != 0
                    ? $"{(volumeSerial >> 16):X4}-{(volumeSerial & 0xFFFF):X4}"
                    : null;

                long? effectiveFrn = (frnAvailable && rawFrn > 0) ? rawFrn : null;

                // --- h. 构建墓碑键并查询缓存 ---
                string? frnKey = (effectiveFrn.HasValue && volumeGuid is not null)
                    ? $"{volumeGuid}:{effectiveFrn.Value}"
                    : null;

                // 指纹键降级：当 FRN 键构造失败时（全局 FRN 不可用、或单文件 rawFrn=0、
                // 或 volumeGuid=null），自动降级为 Size+LastWriteTime 指纹匹配。
                // 不依赖全局 frnAvailable 标记，因为全局可用时特定文件仍可能无有效 FRN
                // （如 FAT32/exFAT 卷上的文件）。
                string? fingerprintKey = (frnKey is null)
                    ? $"{sizeBytes}:{lastWriteTime:O}"
                    : null;

                if (_tombstoneCache.IsTombstoned(frnKey, fingerprintKey, normalizedPath, sizeBytes))
                {
                    filesSkippedByTombstone++;
                    continue;
                }

                // --- i. 构建 EverythingSortKey（文件路径，可用于后续 Everything 重查）---
                string sortKey = normalizedPath;

                // --- j. yield return 产出 FileItem ---
                totalBytesYielded += sizeBytes;
                filesYielded++;

                yield return new FileItem
                {
                    FilePath = normalizedPath,
                    SizeBytes = sizeBytes,
                    LastWriteTime = lastWriteTime,
                    Extension = extension,
                    VolumeGuid = volumeGuid,
                    FileReferenceNumber = effectiveFrn,
                    EverythingSortKey = sortKey
                };
            }

            // 记录枚举完成统计摘要
            _logger.LogInformation(
                "EnumerateFilesAsync 完成: 产出={Yielded}, 墓碑跳过={TombstoneSkip}, " +
                "过滤器跳过={FilterSkip}, 目录跳过={DirSkip}, 总计字节={TotalBytes}",
                filesYielded, filesSkippedByTombstone,
                filesSkippedByFilter, filesSkippedAsDirectory,
                totalBytesYielded);
        }
        finally
        {
            _sdkLock.Release();
        }
    }

    // ================================================================
    // 文件变更轮询
    // ================================================================

    /// <summary>
    /// 启动文件变更轮询定时器。
    /// 在 Everything 依赖检测通过后由控制层调用。
    /// 重复调用幂等安全 — 若定时器已创建则直接返回。
    /// </summary>
    public void StartChangePolling()
    {
        if (_disposed)
        {
            _logger.LogWarning("StartChangePolling 在已释放的 Provider 上调用，忽略");
            return;
        }

        if (_changePollTimer != null)
        {
            _logger.LogDebug("变更轮询已启动，忽略重复调用");
            return;
        }

        _changePollTimer = new Timer(
            callback: OnChangePollingTimerElapsed,
            state: null,
            dueTime: ChangePollIntervalMs,
            period: ChangePollIntervalMs);

        _logger.LogInformation("文件变更轮询已启动，间隔 {Interval}ms", ChangePollIntervalMs);
    }

    /// <summary>
    /// 定时器回调：轮询 Everything 自上次检查以来修改的文件。
    ///
    /// 使用 Everything 的 dm: (date modified) 查询语法，
    /// 获取自 _lastPollTimeUtc 以来修改的所有文件。
    /// 非阻塞尝试获取 SDK 锁 — 若正在枚举中则跳过本次轮询。
    /// 所有异常在此方法内捕获，不会导致定时器停止。
    /// </summary>
    private void OnChangePollingTimerElapsed(object? state)
    {
        if (_disposed || _changePollTimer == null)
        {
            return;
        }

        // 非阻塞尝试获取 SDK 锁 — 若枚举正在进行中，跳过本次轮询
        if (!_sdkLock.Wait(0))
        {
            return;
        }

        try
        {
            PollForFileChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "文件变更轮询发生异常");
        }
        finally
        {
            _sdkLock.Release();
        }
    }

    /// <summary>
    /// 执行一次文件变更轮询。
    /// 构建 dm:&gt;{上次轮询时间} 查询，为每个结果触发 FileChanged 事件。
    /// 调用方必须持有 _sdkLock。
    /// </summary>
    private void PollForFileChanges()
    {
        DateTime currentPollTime = DateTime.UtcNow;

        // 构建按修改时间过滤的 Everything 查询
        string timeQuery = $"dm:>{_lastPollTimeUtc.ToString(EverythingDateTimeFormat)}";
        _lastPollTimeUtc = currentPollTime;

        // 清除前序操作可能设置的 Max 限制，确保轮询查询返回所有变更文件
        _sdk.ResetMax();

        _sdk.SetRequestFlags(EverythingNative.REQUEST_PATH);
        _sdk.SetSearch(timeQuery);

        if (!_sdk.Query(true))
        {
            return;
        }

        uint count = _sdk.GetNumResults();
        if (count == 0)
        {
            return;
        }

        // 限制单次轮询产出的事件数量，防止事件风暴
        int eventCount = (int)Math.Min(count, MaxChangeEventsPerPoll);
        var firedEvents = new List<FileChangedEventArgs>(eventCount);
        var pathBuffer = new StringBuilder(PathBufferSize);

        for (uint i = 0; i < eventCount; i++)
        {
            pathBuffer.Clear();
            _sdk.GetResultFullPathName(i, pathBuffer, PathBufferSize);
            string rawPath = pathBuffer.ToString();

            if (string.IsNullOrEmpty(rawPath))
            {
                continue;
            }

            string normalizedPath;
            try
            {
                normalizedPath = PathNormalizer.Normalize(rawPath);
            }
            catch (ArgumentException)
            {
                continue;
            }

            firedEvents.Add(new FileChangedEventArgs
            {
                FilePath = normalizedPath,
                // 轮询只能检测到"存在"的文件，无法区分 Created 和 Modified
                // 因此统一标记为 Modified（C2 视图层将按路径刷新对应行）
                ChangeType = FileChangeType.Modified,
                Timestamp = currentPollTime
            });
        }

        // 在锁外触发事件（事件处理程序可能较慢，不应阻塞 SDK 访问）
        foreach (var evt in firedEvents)
        {
            try
            {
                FileChanged?.Invoke(this, evt);
            }
            catch (Exception ex)
            {
                // 单个事件处理程序的异常不应影响其他事件
                _logger.LogError(ex, "FileChanged 事件处理异常: {FilePath}", evt.FilePath);
            }
        }

        _logger.LogDebug("文件变更轮询完成: 触发 {Count} 个事件 (总计 {Total} 条变更)",
            firedEvents.Count, count);
    }

    // ================================================================
    // 搜索表达式构建
    // ================================================================

    /// <summary>
    /// 构建 Everything 搜索表达式，将 PathFilter 下推到 Everything 端。
    /// </summary>
    /// <remarks>
    /// Everything 搜索语法中，直接包含路径（如 <c>"C:\Users\AppData\"</c>）即可将结果
    /// 限制在该目录下。将 PathFilter 下推到 Everything 端可避免返回全系统文件后在 C# 侧
    /// 过滤，导致 MaxResults 上限被无关条目消耗而遗漏目标目录中的实际文件。
    ///
    /// PathFilter 的去标准化：内部 PathFilter 使用 \\?\ 扩展前缀格式，
    /// 需通过 Denormalize 还原为常规 Windows 路径格式后传递给 Everything。
    /// </remarks>
    /// <param name="queryParams">查询参数（含 SearchExpression 和可选 PathFilter）</param>
    /// <returns>合并路径过滤后的 Everything 搜索表达式</returns>
    private static string BuildSearchExpression(EverythingQueryParams queryParams)
    {
        string search = queryParams.SearchExpression ?? string.Empty;

        if (queryParams.PathFilter is not null)
        {
            // 去除 \\?\ 前缀，还原为 Everything 可识别的常规路径格式
            string normalPath = PathNormalizer.Denormalize(queryParams.PathFilter);

            // 确保尾部带 \ 以精确匹配目录下的内容，
            // 避免 C:\AppData 错误匹配 C:\AppDataBackup\file
            if (!normalPath.EndsWith('\\'))
            {
                normalPath += '\\';
            }

            // 用双引号包裹路径，防止路径中的空格或特殊字符（&、|、(、) 等）
            // 被 Everything 解析为搜索运算符
            string quotedPath = $"\"{normalPath}\"";

            // 路径优先，搜索词紧随其后（Everything 空格分隔语法）
            search = string.IsNullOrEmpty(search)
                ? quotedPath
                : $"{quotedPath} {search}";
        }

        return search;
    }

    // ================================================================
    // IDisposable
    // ================================================================

    /// <summary>
    /// 释放 EverythingFileListProvider 占用的资源。
    /// 停止变更轮询定时器并释放 SDK 访问锁。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _changePollTimer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "释放变更轮询定时器时发生异常");
        }

        _logger.LogMethodExit("EverythingFileListProvider 已释放");
    }
}
