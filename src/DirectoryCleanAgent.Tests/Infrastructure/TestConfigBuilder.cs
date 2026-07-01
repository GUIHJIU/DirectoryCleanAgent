// ============================================================
// TestConfigBuilder.cs
// 功能: UserConfig 建造器，统一测试配置的创建
// 解决各测试类中 UserConfig 初始化代码重复的问题
// 使用 Fluent API 风格，支持链式调用
// ============================================================

using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Tests.Infrastructure;

/// <summary>
/// UserConfig 建造器 — 为集成测试和性能测试提供预配置的 UserConfig 实例
/// 所有默认值与产品代码中的 UserConfig 默认值一致
/// </summary>
public sealed class TestConfigBuilder
{
    private readonly UserConfig _config = new();

    private TestConfigBuilder() { }

    /// <summary>创建默认配置（所有值均为产品代码默认值）</summary>
    public static TestConfigBuilder Create() => new();

    /// <summary>构建并返回 UserConfig 实例</summary>
    public UserConfig Build() => _config.Clone();

    // ============================================================
    // 通用设置
    // ============================================================

    /// <summary>设置数据存储路径</summary>
    public TestConfigBuilder WithDataStorage(string path)
    {
        _config.DataStoragePath = path;
        return this;
    }

    /// <summary>设置语言</summary>
    public TestConfigBuilder WithLanguage(string language)
    {
        _config.Language = language;
        return this;
    }

    // ============================================================
    // 扫描与清理设置
    // ============================================================

    /// <summary>设置删除方式</summary>
    public TestConfigBuilder WithDeleteMethod(DeleteMethod method)
    {
        _config.DeleteMethod = method;
        return this;
    }

    /// <summary>设置是否二次确认</summary>
    public TestConfigBuilder WithConfirmBeforeClean(bool confirm)
    {
        _config.ConfirmBeforeOneClickClean = confirm;
        return this;
    }

    // ============================================================
    // AI 设置
    // ============================================================

    /// <summary>启用 AI（使用自定义服务）</summary>
    public TestConfigBuilder WithAIEnabled(string apiUrl = "https://localhost:8080/v1", string apiKey = "test-key")
    {
        _config.AIEnabled = true;
        _config.AIServiceType = "custom";
        _config.AIApiUrl = apiUrl;
        _config.AIApiKey = apiKey;
        return this;
    }

    /// <summary>禁用 AI</summary>
    public TestConfigBuilder WithAIDisabled()
    {
        _config.AIEnabled = false;
        return this;
    }

    /// <summary>设置 AI 信任级别</summary>
    public TestConfigBuilder WithAITrustLevel(AITrustLevel level)
    {
        _config.AITrustLevel = level;
        return this;
    }

    /// <summary>设置 AI 每日限额</summary>
    public TestConfigBuilder WithAIDailyLimit(int limit)
    {
        _config.AIDailyLimit = limit;
        return this;
    }

    // ============================================================
    // 排除与保护设置
    // ============================================================

    /// <summary>设置隔离区配置</summary>
    public TestConfigBuilder WithQuarantine(long maxSizeBytes, int retentionDays)
    {
        _config.QuarantineMaxSizeBytes = maxSizeBytes;
        _config.QuarantineRetentionDays = retentionDays;
        return this;
    }

    /// <summary>添加排除目录</summary>
    public TestConfigBuilder WithExcludedDir(string dir)
    {
        _config.UserExcludedDirs.Add(dir);
        return this;
    }

    /// <summary>添加排除扩展名</summary>
    public TestConfigBuilder WithExcludedExtension(string ext)
    {
        _config.UserExcludedExtensions.Add(ext);
        return this;
    }

    // ============================================================
    // 系统运行时标记
    // ============================================================

    /// <summary>设置规则缓存版本</summary>
    public TestConfigBuilder WithRuleCacheVersion(int version)
    {
        _config.RuleCacheVersion = version;
        return this;
    }

    /// <summary>设置 FRN 能力标记</summary>
    public TestConfigBuilder WithFrnAvailable(bool available)
    {
        _config.FRN_AVAILABLE = available;
        return this;
    }

    // ============================================================
    // 用户画像
    // ============================================================

    /// <summary>设置为专家模式</summary>
    public TestConfigBuilder WithExpertMode()
    {
        _config.UserMode = UserMode.Expert;
        return this;
    }

    /// <summary>标记为已完成首次配置（跳过向导）</summary>
    public TestConfigBuilder WithFirstRunCompleted()
    {
        _config.IsFirstRun = false;
        return this;
    }

    // ============================================================
    // 预定义配置快捷方法
    // ============================================================

    /// <summary>完整的测试默认配置（FRN 可用、隔离区 10GB/30天、非首次运行）</summary>
    public static UserConfig CreateDefault(string dataStoragePath)
    {
        return Create()
            .WithDataStorage(dataStoragePath)
            .WithFrnAvailable(true)
            .WithQuarantine(10L * 1024 * 1024 * 1024, 30)
            .WithFirstRunCompleted()
            .Build();
    }

    /// <summary>AI 测试专用配置</summary>
    public static UserConfig CreateForAITesting(string dataStoragePath)
    {
        return Create()
            .WithDataStorage(dataStoragePath)
            .WithAIEnabled()
            .WithAITrustLevel(AITrustLevel.Low)
            .WithAIDailyLimit(500)
            .WithFrnAvailable(true)
            .WithFirstRunCompleted()
            .Build();
    }
}
