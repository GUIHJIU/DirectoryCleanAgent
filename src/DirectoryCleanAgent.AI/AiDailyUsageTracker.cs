using System.Text.Json;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Logging;

namespace DirectoryCleanAgent.AI;

/// <summary>
/// AI 每日调用次数持久化追踪器。
///
/// 将每日计数存储到独立 JSON 文件（不污染 UserConfig）：
///   {DataStoragePath}/ai_usage.json
///   格式：{"date":"2026-06-23","count":15}
///
/// 日期变更时自动重置计数。
/// 线程安全：SemaphoreSlim(1,1) 序列化所有读写操作。
/// 原子写入：先写临时文件，成功后再替换目标文件（防写崩溃损坏数据）。
/// </summary>
internal sealed class AiDailyUsageTracker : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly int _dailyLimit;
    private readonly ILogger<AiDailyUsageTracker> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string _currentDate;
    private int _currentCount;
    private bool _disposed;

    public AiDailyUsageTracker(string dataStoragePath, int dailyLimit, ILogger<AiDailyUsageTracker> logger)
    {
        _dailyLimit = dailyLimit;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 确保数据目录存在
        var dir = string.IsNullOrWhiteSpace(dataStoragePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceClear")
            : dataStoragePath;

        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "ai_usage.json");
        _currentDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        _logger.LogDebug("AI 每日用量追踪器初始化: 文件={Path}, 日限={Limit}", _filePath, _dailyLimit);
    }

    /// <summary>今日已用调用次数（线程安全，与异步方法共用 SemaphoreSlim）</summary>
    public int UsedCount
    {
        get
        {
            _lock.Wait();
            try { return _currentCount; }
            finally { _lock.Release(); }
        }
    }

    /// <summary>今日剩余调用次数</summary>
    public int RemainingCount => Math.Max(0, _dailyLimit - UsedCount);

    /// <summary>是否已达每日限额</summary>
    public bool IsLimitReached => UsedCount >= _dailyLimit;

    /// <summary>
    /// 加载持久化计数文件。若文件不存在或损坏则初始化为 0。
    /// 若日期已变更则重置计数。
    /// </summary>
    public async Task LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            if (!File.Exists(_filePath))
            {
                _currentDate = today;
                _currentCount = 0;
                _logger.LogDebug("AI 用量文件不存在，初始化为 0");
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_filePath);
                var record = JsonSerializer.Deserialize<UsageRecord>(json, JsonOptions);

                if (record == null)
                {
                    _logger.LogWarning("AI 用量文件为空 JSON，重置为 0");
                    _currentDate = today;
                    _currentCount = 0;
                }
                else if (!string.Equals(record.Date, today, StringComparison.Ordinal))
                {
                    // 日期已变更，重置计数
                    _logger.LogInformation("AI 用量日期变更: {OldDate}→{NewDate}，重置计数（昨日={OldCount}）",
                        record.Date, today, record.Count);
                    _currentDate = today;
                    _currentCount = 0;
                }
                else
                {
                    _currentDate = today;
                    _currentCount = Math.Min(record.Count, _dailyLimit); // 防止反序列化出超过日限的值
                    _logger.LogDebug("AI 用量已加载: {Date} → {Count}/{Limit}", _currentDate, _currentCount, _dailyLimit);
                }
            }
            catch (JsonException ex)
            {
                // JSON 损坏 → 备份损坏文件 + 重置
                _logger.LogWarning(ex, "AI 用量文件 JSON 损坏，备份并重置");
                BackupCorruptedFile();
                _currentDate = today;
                _currentCount = 0;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 原子递增今日计数并持久化。
    /// 若已达日限则记录警告但仍返回当前计数（不拒绝，由调用方决定是否调用 API）。
    /// </summary>
    /// <returns>递增后的今日计数</returns>
    public async Task<int> IncrementAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            // 日期变更时自动重置
            if (!string.Equals(_currentDate, today, StringComparison.Ordinal))
            {
                _logger.LogInformation("AI 用量日期变更: {OldDate}→{NewDate}，重置计数", _currentDate, today);
                _currentDate = today;
                _currentCount = 0;
            }

            _currentCount++;

            if (_currentCount > _dailyLimit)
            {
                _logger.LogWarning("AI 日限已超出: {Count}/{Limit}", _currentCount, _dailyLimit);
            }

            // 原子写入：先写临时文件，再替换
            var record = new UsageRecord { Date = _currentDate, Count = _currentCount };
            var json = JsonSerializer.Serialize(record, JsonOptions);
            var tmpPath = _filePath + ".tmp";

            await File.WriteAllTextAsync(tmpPath, json);
            File.Move(tmpPath, _filePath, overwrite: true);

            _logger.LogDebug("AI 用量递增: {Count}/{Limit}", _currentCount, _dailyLimit);
            return _currentCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 用量文件写入失败");
            return _currentCount; // 内存中的计数仍然准确，只是未持久化
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>备份损坏的用量文件</summary>
    private void BackupCorruptedFile()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var backupPath = _filePath + ".corrupted." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(_filePath, backupPath);
                _logger.LogInformation("损坏的 AI 用量文件已备份为: {Path}", backupPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "备份损坏的 AI 用量文件失败");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
    }

    /// <summary>持久化文件记录结构</summary>
    private sealed class UsageRecord
    {
        public string Date { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
