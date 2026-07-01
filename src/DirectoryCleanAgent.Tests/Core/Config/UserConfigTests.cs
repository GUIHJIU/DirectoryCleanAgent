using System.Text.Json;

namespace DirectoryCleanAgent.Tests.Core.Config;

/// <summary>
/// UserConfig 序列化/反序列化及默认值验证测试。
/// </summary>
public class UserConfigTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [Fact]
    public void DefaultConfig_AllProperties_HaveExpectedDefaults()
    {
        // Arrange & Act
        var config = new UserConfig();

        // Assert — 通用
        Assert.Equal(StartupBehavior.AutoScan, config.StartupBehavior);
        Assert.Equal(ThemeType.FollowSystem, config.Theme);
        Assert.Equal("zh-CN", config.Language);

        // Assert — 扫描与清理
        Assert.Equal(ScanMode.SmartScanSystemDrive, config.ScanMode);
        Assert.Equal(DeleteMethod.RecycleBin, config.DeleteMethod);
        Assert.True(config.ConfirmBeforeOneClickClean);
        Assert.True(config.ShowManualReviewFiles);
        Assert.True(config.AutoRefreshScanResults);
        Assert.NotNull(config.IncludedVolumes);
        Assert.Empty(config.IncludedVolumes);

        // Assert — AI
        Assert.False(config.AIEnabled);
        Assert.Equal("custom", config.AIServiceType);
        Assert.Equal("gpt-3.5-turbo", config.AIModel);
        Assert.Equal(AITrustLevel.Medium, config.AITrustLevel);
        Assert.Equal(50, config.AIDailyLimit);
        Assert.False(config.AIAutoAnalyze);

        // Assert — 排除与保护
        Assert.NotNull(config.UserExcludedDirs);
        Assert.NotNull(config.UserExcludedExtensions);
        Assert.Empty(config.UserExcludedDirs);
        Assert.Empty(config.UserExcludedExtensions);
        Assert.Equal(10L * 1024 * 1024 * 1024, config.QuarantineMaxSizeBytes);
        Assert.Equal(30, config.QuarantineRetentionDays);

        // Assert — 系统标记
        Assert.Equal(0, config.RuleCacheVersion);
        Assert.True(config.FRN_AVAILABLE);

        // Assert — 模式
        Assert.Equal(UserMode.Simple, config.UserMode);
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesAllValues()
    {
        // Arrange
        var original = new UserConfig
        {
            Language = "en-US",
            Theme = ThemeType.Dark,
            UserMode = UserMode.Expert,
            AIEnabled = true,
            AIModel = "gpt-4",
            AITrustLevel = AITrustLevel.High,
            IncludedVolumes = new List<string> { "C:", "D:" },
            UserExcludedDirs = new List<string> { "C:\\Temp", "D:\\Cache" },
            RuleCacheVersion = 5,
            FRN_AVAILABLE = false,
            QuarantineMaxSizeBytes = 5L * 1024 * 1024 * 1024,
            QuarantineRetentionDays = 7
        };

        // Act — 序列化
        var json = JsonSerializer.Serialize(original, _jsonOptions);

        // Act — 反序列化
        var restored = JsonSerializer.Deserialize<UserConfig>(json, _jsonOptions);

        // Assert
        Assert.NotNull(restored);
        Assert.Equal("en-US", restored!.Language);
        Assert.Equal(ThemeType.Dark, restored.Theme);
        Assert.Equal(UserMode.Expert, restored.UserMode);
        Assert.True(restored.AIEnabled);
        Assert.Equal("gpt-4", restored.AIModel);
        Assert.Equal(AITrustLevel.High, restored.AITrustLevel);
        Assert.Equal(new[] { "C:", "D:" }, restored.IncludedVolumes);
        Assert.Equal(new[] { "C:\\Temp", "D:\\Cache" }, restored.UserExcludedDirs);
        Assert.Equal(5, restored.RuleCacheVersion);
        Assert.False(restored.FRN_AVAILABLE);
        Assert.Equal(5L * 1024 * 1024 * 1024, restored.QuarantineMaxSizeBytes);
        Assert.Equal(7, restored.QuarantineRetentionDays);
    }

    [Fact]
    public void Serialize_DefaultConfig_ProducesValidJson()
    {
        // Arrange
        var config = new UserConfig();

        // Act
        var json = JsonSerializer.Serialize(config, _jsonOptions);

        // Assert
        Assert.NotNull(json);
        Assert.Contains("\"language\"", json);
        Assert.Contains("\"zh-CN\"", json);
        Assert.Contains("\"startupBehavior\"", json);

        // 验证 JSON 可以被反序列化
        var restored = JsonSerializer.Deserialize<UserConfig>(json, _jsonOptions);
        Assert.NotNull(restored);
    }

    [Fact]
    public void Clone_CreatesIndependentCopy()
    {
        // 验证：Clone 返回的副本与原始实例不共享可变集合
        // Arrange
        var original = new UserConfig
        {
            Language = "en-US",
            IncludedVolumes = new List<string> { "C:" },
            UserExcludedDirs = new List<string> { "C:\\Temp" },
            UserExcludedExtensions = new List<string> { ".log" },
            RuleCacheVersion = 5
        };

        // Act
        var clone = original.Clone();

        // 修改副本
        clone.Language = "zh-CN";
        clone.IncludedVolumes.Add("D:");
        clone.UserExcludedDirs.Clear();
        clone.UserExcludedExtensions.Remove(".log");
        clone.RuleCacheVersion = 10;

        // Assert — 原始实例不受影响
        Assert.Equal("en-US", original.Language);
        Assert.Single(original.IncludedVolumes);
        Assert.Equal("C:", original.IncludedVolumes[0]);
        Assert.Single(original.UserExcludedDirs);
        Assert.Single(original.UserExcludedExtensions);
        Assert.Equal(5, original.RuleCacheVersion);
    }

    [Fact]
    public void Deserialize_FromMinimalJson_UsesDefaultsForMissingFields()
    {
        // Arrange — 最小 JSON，仅包含部分字段
        const string minimalJson = """
        {
            "language": "en-US",
            "userMode": 1,
            "aiEnabled": true
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<UserConfig>(minimalJson, _jsonOptions);

        // Assert — 显式字段
        Assert.NotNull(config);
        Assert.Equal("en-US", config!.Language);
        Assert.Equal(UserMode.Expert, config.UserMode);
        Assert.True(config.AIEnabled);

        // Assert — 缺失字段使用默认值
        Assert.Equal(StartupBehavior.AutoScan, config.StartupBehavior);
        Assert.Equal(DeleteMethod.RecycleBin, config.DeleteMethod);
        Assert.Equal(0, config.RuleCacheVersion);
    }
}
