using System.IO;

namespace DirectoryCleanAgent.Core.Config;

/// <summary>
/// 规则目录 FileSystemWatcher 实现，带 500ms 防抖。
///
/// 内部架构：
///   FileSystemWatcher (OS 层)
///       │  Changed/Created/Deleted/Renamed (ThreadPool 线程)
///       ▼
///   _debounceTimer.Change(500ms, Infinite)   ← 重置防抖计时器
///       │  (500ms 内无新事件)
///       ▼
///   _configService.IncrementRuleCacheVersion()  ← 触发版本递增
///
/// Buffer overflow 时（Error 事件）：
///   立即调用 IncrementRuleCacheVersion() + 重启 FileSystemWatcher。
///
/// 线程安全：
///   - _lock 仅保护 StartAsync/StopAsync/Error 重启等生命周期操作
///   - System.Threading.Timer.Change() 自身线程安全
///   - IConfigService.IncrementRuleCacheVersion() 内部有 SemaphoreSlim 保护
/// </summary>
public sealed class RulesDirectoryWatcher : IRulesDirectoryWatcher
{
    private readonly IConfigService _configService;
    private readonly ILogger<RulesDirectoryWatcher> _logger;
    private readonly object _lock = new();

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private string _rulesDirectoryPath;
    private int _debounceIntervalMs = 500;
    private bool _isRunning;
    private bool _disposed;

    /// <summary>JSON 规则文件过滤</summary>
    private const string JsonFilter = "*.json";
    /// <summary>默认规则子目录名</summary>
    private const string DefaultRulesSubDirectory = "rules";

    /// <summary>
    /// 获取默认规则目录路径：%LocalAppData%\SpaceClear\rules\
    /// </summary>
    public static string DefaultRulesDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpaceClear",
            DefaultRulesSubDirectory);

    public RulesDirectoryWatcher(
        IConfigService configService,
        ILogger<RulesDirectoryWatcher> logger)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rulesDirectoryPath = DefaultRulesDirectory;
    }

    // ============================================================
    // 公开属性
    // ============================================================

    /// <inheritdoc />
    public string RulesDirectoryPath
    {
        get => _rulesDirectoryPath;
        set
        {
            ThrowIfDisposed();
            if (_isRunning)
            {
                // 运行中不允许修改路径，避免 FileSystemWatcher 处于不一致状态
                throw new InvalidOperationException("不能在监控运行期间修改 RulesDirectoryPath。请先调用 StopAsync。");
            }
            _rulesDirectoryPath = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    /// <inheritdoc />
    public int DebounceIntervalMs
    {
        get => _debounceIntervalMs;
        set
        {
            ThrowIfDisposed();
            if (_isRunning)
            {
                throw new InvalidOperationException("不能在监控运行期间修改 DebounceIntervalMs。请先调用 StopAsync。");
            }
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "防抖间隔不能为负数");
            _debounceIntervalMs = value;
        }
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _isRunning;
            }
        }
    }

    // ============================================================
    // 公开生命周期
    // ============================================================

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_isRunning)
            {
                _logger.LogDebug("监控已在运行中，跳过重复启动");
                return Task.CompletedTask;
            }

            try
            {
                // 确保规则目录存在
                if (!Directory.Exists(_rulesDirectoryPath))
                {
                    Directory.CreateDirectory(_rulesDirectoryPath);
                    _logger.LogInformation("创建规则目录: {Path}", _rulesDirectoryPath);
                }

                // 初始化防抖 Timer（单次触发模式，period = Infinite）
                _debounceTimer = new Timer(
                    callback: OnDebounceTimerElapsed,
                    state: null,
                    dueTime: Timeout.Infinite,
                    period: Timeout.Infinite);

                // 初始化 FileSystemWatcher
                // InternalBufferSize 设为 64KB（Windows 默认 8KB），降低大容量变更时的溢出风险
                _watcher = new FileSystemWatcher(_rulesDirectoryPath, JsonFilter)
                {
                    NotifyFilter = NotifyFilters.LastWrite
                                 | NotifyFilters.FileName
                                 | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = false,
                    InternalBufferSize = 64 * 1024
                };

                // 订阅事件
                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Deleted += OnFileChanged;
                _watcher.Renamed += OnFileRenamed;
                _watcher.Error += OnWatcherError;

                // 启动事件触发
                _watcher.EnableRaisingEvents = true;
                _isRunning = true;

                _logger.LogInformation(
                    "规则目录监控已启动: {Path}，防抖 {DebounceMs}ms，过滤器 {Filter}",
                    _rulesDirectoryPath, _debounceIntervalMs, JsonFilter);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动规则目录监控失败: {Path}", _rulesDirectoryPath);
                // 清理已部分初始化的资源
                CleanupInternal();
                throw;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_isRunning)
            {
                _logger.LogDebug("监控未在运行，跳过 StopAsync");
                return Task.CompletedTask;
            }

            _logger.LogInformation("正在停止规则目录监控: {Path}", _rulesDirectoryPath);
            CleanupInternal();
            _isRunning = false;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;

            _logger.LogDebug("释放 RulesDirectoryWatcher 资源");
            CleanupInternal();
            _isRunning = false;
        }
    }

    // ============================================================
    // FileSystemWatcher 事件处理器
    // ============================================================

    /// <summary>
    /// 处理 Created / Changed / Deleted 事件。
    /// 每次事件到达时重置防抖计时器，合并连续写入。
    /// </summary>
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            _logger.LogDebug(
                "检测到规则文件变更: {ChangeType} — {FilePath}",
                e.ChangeType, e.FullPath);

            // 重置防抖计时器 — Change() 自身线程安全
            _debounceTimer?.Change(_debounceIntervalMs, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            // 事件处理器内部异常不应让 FileSystemWatcher 崩溃，
            // 但记录异常以便诊断
            _logger.LogError(ex,
                "处理文件变更事件异常: {ChangeType} — {FilePath}",
                e.ChangeType, e.FullPath);
        }
    }

    /// <summary>
    /// 处理 Renamed 事件。
    /// 文件重命名也可能改变规则集，需要触发版本递增。
    /// </summary>
    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            _logger.LogDebug(
                "检测到规则文件重命名: {OldPath} → {NewPath}",
                e.OldFullPath, e.FullPath);

            _debounceTimer?.Change(_debounceIntervalMs, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "处理文件重命名事件异常: {OldPath} → {NewPath}",
                e.OldFullPath, e.FullPath);
        }
    }

    /// <summary>
    /// 处理 FileSystemWatcher.Error 事件（内部缓冲区溢出）。
    ///
    /// 缓冲区溢出意味着我们丢失了具体的变更事件，但可以确定"有文件发生了变更"。
    /// 因此立即触发一次版本递增，然后重启 FileSystemWatcher 以清空缓冲区。
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        try
        {
            _logger.LogError(
                e.GetException(),
                "FileSystemWatcher 内部错误（可能缓冲区溢出），立即触发版本递增并重启: {Path}",
                _rulesDirectoryPath);

            // 发生了变更但丢失了具体事件 → 立即触发版本递增
            try
            {
                _configService.IncrementRuleCacheVersion();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error 事件后 IncrementRuleCacheVersion 调用失败");
            }

            // 重启 FileSystemWatcher 以清空内部缓冲区
            lock (_lock)
            {
                if (_disposed || !_isRunning)
                    return;

                try
                {
                    CleanupInternal();
                    // 重新启动（递归调用 StartAsync 的核心逻辑，但已持有 _lock）
                    _debounceTimer = new Timer(
                        OnDebounceTimerElapsed, null,
                        Timeout.Infinite, Timeout.Infinite);

                    _watcher = new FileSystemWatcher(_rulesDirectoryPath, JsonFilter)
                    {
                        NotifyFilter = NotifyFilters.LastWrite
                                     | NotifyFilters.FileName
                                     | NotifyFilters.CreationTime,
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = false,
                        InternalBufferSize = 64 * 1024
                    };
                    _watcher.Changed += OnFileChanged;
                    _watcher.Created += OnFileChanged;
                    _watcher.Deleted += OnFileChanged;
                    _watcher.Renamed += OnFileRenamed;
                    _watcher.Error += OnWatcherError;
                    _watcher.EnableRaisingEvents = true;

                    _logger.LogInformation("FileSystemWatcher 已重启: {Path}", _rulesDirectoryPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "重启 FileSystemWatcher 失败，监控将停止: {Path}", _rulesDirectoryPath);
                    CleanupInternal();
                    _isRunning = false;
                }
            }
        }
        catch (Exception ex)
        {
            // OnWatcherError 本身的最外层保护
            _logger.LogError(ex, "OnWatcherError 处理器自身异常");
        }
    }

    // ============================================================
    // 防抖计时器回调
    // ============================================================

    /// <summary>
    /// 防抖计时器回调。
    /// 经过 DebounceIntervalMs 无新事件后触发，调用 IncrementRuleCacheVersion。
    /// </summary>
    /// <param name="state">未使用（Timer 回调签名要求）</param>
    private void OnDebounceTimerElapsed(object? state)
    {
        try
        {
            _logger.LogDebug("防抖计时器到期，触发规则版本递增");

            // System.Threading.Timer 回调在 ThreadPool 线程上执行，
            // 直接调用 IConfigService（其内部有 SemaphoreSlim 保证线程安全）
            _configService.IncrementRuleCacheVersion();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "防抖回调中 IncrementRuleCacheVersion 调用失败");
        }
    }

    // ============================================================
    // 私有辅助方法
    // ============================================================

    /// <summary>
    /// 清理 FileSystemWatcher 和 Timer 资源。
    /// 调用方必须持有 _lock。
    /// </summary>
    private void CleanupInternal()
    {
        // 先停止 Timer，防止在清理 FSW 期间触发回调
        if (_debounceTimer != null)
        {
            try
            {
                // 设为不触发，等待可能正在执行的回调完成
                _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _debounceTimer.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放防抖 Timer 时异常");
            }
            _debounceTimer = null;
        }

        // 停止并释放 FileSystemWatcher
        if (_watcher != null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileChanged;
                _watcher.Created -= OnFileChanged;
                _watcher.Deleted -= OnFileChanged;
                _watcher.Renamed -= OnFileRenamed;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放 FileSystemWatcher 时异常");
            }
            _watcher = null;
        }
    }

    /// <summary>检查是否已释放，已释放则抛出 ObjectDisposedException</summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RulesDirectoryWatcher));
    }
}
