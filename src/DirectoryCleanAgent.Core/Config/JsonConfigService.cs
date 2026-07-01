using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DirectoryCleanAgent.Core.Config;

/// <summary>
/// JSON 文件配置服务的默认实现。
/// 配置文件存储于 %LocalAppData%\SpaceClear\config.json。
/// 采用原子写入（临时文件 + Move 替换）保护配置完整性。
/// </summary>
public class JsonConfigService : IConfigService, IDisposable
{
    private readonly ILogger<JsonConfigService> _logger;
    private readonly string _configDirectory;
    private readonly string _configFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    private UserConfig _current = new();

    // 用于线程安全的读写锁
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>配置文件默认目录</summary>
    public static string DefaultConfigDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpaceClear");

    /// <summary>配置文件名</summary>
    public const string ConfigFileName = "config.json";

    public JsonConfigService(ILogger<JsonConfigService> logger)
        : this(logger, DefaultConfigDirectory)
    {
    }

    public JsonConfigService(ILogger<JsonConfigService> logger, string configDirectory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configDirectory = configDirectory ?? throw new ArgumentNullException(nameof(configDirectory));
        _configFilePath = Path.Combine(_configDirectory, ConfigFileName);

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,           // 格式化 JSON 便于人工阅读和调试
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    // ============================================================
    // 公开属性
    // ============================================================

    /// <inheritdoc />
    public UserConfig Current => _current;

    /// <inheritdoc />
    public string ConfigFilePath => _configFilePath;

    /// <inheritdoc />
    public event EventHandler? ConfigChanged;

    // ============================================================
    // 加载与保存
    // ============================================================

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("正在从 {Path} 加载配置...", _configFilePath);

            // 若配置文件不存在，创建包含默认值的全新配置并写入磁盘
            if (!File.Exists(_configFilePath))
            {
                _logger.LogWarning("配置文件不存在，创建默认配置");
                _current = new UserConfig();
                // 默认系统盘
                if (_current.IncludedVolumes.Count == 0)
                {
                    var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
                    _current.IncludedVolumes.Add(systemDrive);
                }
                await SaveInternalAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await ReadFromFileAsync(ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "配置加载完成。语言={Language}, 模式={Mode}, 规则版本={Version}",
                _current.Language, _current.UserMode, _current.RuleCacheVersion);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("配置加载被取消");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载配置文件失败: {Path}，将使用默认配置", _configFilePath);
            _current = new UserConfig();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveInternalAsync(ct).ConfigureAwait(false);
            OnConfigChanged();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("配置保存被取消");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置文件失败: {Path}", _configFilePath);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public void IncrementRuleCacheVersion()
    {
        // 使用 fire-and-forget 模式，但在 SemaphoreSlim 内部完成递增+保存，
        // 确保不会出现"版本号已递增但尚未持久化"的中间态。
        _ = Task.Run(async () =>
        {
            try
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    var oldVersion = _current.RuleCacheVersion;
                    _current.RuleCacheVersion++;
                    _logger.LogInformation(
                        "规则缓存版本递增：{OldVersion} → {NewVersion}",
                        oldVersion, _current.RuleCacheVersion);

                    await SaveInternalAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _semaphore.Release();
                }

                // ConfigChanged 在锁外触发，防止事件处理器回调 IConfigService 时死锁
                OnConfigChanged();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "规则版本递增后保存配置失败");
            }
        });
    }

    // ============================================================
    // 私有方法
    // ============================================================

    /// <summary>
    /// 从文件读取并反序列化配置。
    /// </summary>
    private async Task ReadFromFileAsync(CancellationToken ct)
    {
        using var stream = new FileStream(
            _configFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);

        var config = await JsonSerializer.DeserializeAsync<UserConfig>(
            stream, _jsonOptions, ct).ConfigureAwait(false);

        if (config != null)
        {
            _current = config;
            _logger.LogDebug("配置文件反序列化成功，共 {FieldCount} 个顶级属性",
                typeof(UserConfig).GetProperties().Length);
        }
        else
        {
            _logger.LogWarning("配置文件反序列化返回 null，使用默认配置");
            _current = new UserConfig();
        }

        // 确保 IncludedVolumes 至少包含系统盘
        if (_current.IncludedVolumes.Count == 0)
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
            _current.IncludedVolumes.Add(systemDrive);
            _logger.LogDebug("IncludedVolumes 为空，自动添加系统盘 {Drive}", systemDrive);
        }
    }

    /// <summary>
    /// 以原子方式将配置写入磁盘：先写临时文件（.tmp），成功后再 Move 替换原文件。
    /// 此方式可防止写入过程中断电或崩溃导致配置文件损坏。
    /// </summary>
    private async Task SaveInternalAsync(CancellationToken ct)
    {
        // 确保目录存在
        if (!Directory.Exists(_configDirectory))
        {
            Directory.CreateDirectory(_configDirectory);
            _logger.LogDebug("创建配置目录: {Dir}", _configDirectory);
        }

        var tempFilePath = _configFilePath + ".tmp";

        try
        {
            // 步骤1：写入临时文件
            await using (var stream = new FileStream(
                tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream, _current, _jsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            // 步骤2：原子替换目标文件（Windows 上 File.Move 是原子操作）
            File.Move(tempFilePath, _configFilePath, overwrite: true);

            _logger.LogDebug("配置已写入: {Path}", _configFilePath);
        }
        finally
        {
            // 清理可能残留的临时文件
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理临时配置文件失败: {Path}", tempFilePath);
                }
            }
        }
    }

    /// <summary>触发 ConfigChanged 事件</summary>
    private void OnConfigChanged()
    {
        try
        {
            ConfigChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConfigChanged 事件处理器抛出异常");
        }
    }

    public void Dispose()
    {
        _semaphore?.Dispose();
    }
}
