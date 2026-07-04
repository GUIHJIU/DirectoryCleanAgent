using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Core.Localization;

/// <summary>
/// ILocalizationService 单元测试。
/// 验证本地化字符串检索、语言切换、事件触发、回退和异常处理。
///
/// 注意：此任务为桌面应用本地化基础设施，无 HTTP API 端点。
/// 传统 Postman 测试用例在此场景下不适用，以 xUnit 单元测试替代。
/// </summary>
public class LocalizationServiceTests : IDisposable
{
    private readonly ILogger<LocalizationService> _logger;
    private readonly StubConfigService _configService;
    private readonly LocalizationService _service;

    public LocalizationServiceTests()
    {
        _logger = NullLoggerFactory.Instance.CreateLogger<LocalizationService>();
        _configService = new StubConfigService();
        _service = new LocalizationService(_logger, _configService);
    }

    // ================================================================
    // GetString — 基础检索
    // ================================================================

    /// <summary>
    /// 测试用例：通过有效的资源键获取中文（默认语言）本地化字符串。
    /// 验证 GetString 在默认语言下返回正确的资源值。
    /// 对应 Postman 场景：GET /api/localization/string?key=Dashboard.SafeClean&lang=zh-CN
    /// </summary>
    [Fact]
    public void GetString_ExistingKey_DefaultLanguage_ReturnsChineseString()
    {
        // Arrange — 服务默认语言为 zh-CN
        Assert.Equal("zh-CN", _service.CurrentLanguage);

        // Act
        var result = _service.GetString("Dashboard.SafeClean");

        // Assert
        Assert.False(string.IsNullOrEmpty(result),
            "有效键应返回非空字符串");
        Assert.Equal("安全可自动清理", result);
    }

    /// <summary>
    /// 测试用例：通过有效的资源键获取英文本地化字符串。
    /// 验证 GetString 在英文语言下返回正确的资源值。
    /// 对应 Postman 场景：GET /api/localization/string?key=Dashboard.SafeClean&lang=en-US
    /// </summary>
    [Fact]
    public void GetString_ExistingKey_English_ReturnsEnglishString()
    {
        // Arrange — 切换到英文
        _service.SetLanguage("en-US");
        Assert.Equal("en-US", _service.CurrentLanguage);

        // Act
        var result = _service.GetString("Dashboard.SafeClean");

        // Assert
        Assert.Equal("Safe to Auto-Clean", result);
    }

    /// <summary>
    /// 测试用例：确认所有支持的语言对同一键返回不同的值。
    /// 验证中英文资源文件确实包含不同的文本。
    /// 对应 Postman 场景：循环请求 GET /api/localization/string?key=Common.OK&lang={zh-CN,en-US}
    /// </summary>
    [Fact]
    public void GetString_SameKey_DifferentLanguages_DifferentValues()
    {
        // Act
        var zhResult = _service.GetString("Common.OK", "zh-CN");
        var enResult = _service.GetString("Common.OK", "en-US");

        // Assert
        Assert.NotEqual(zhResult, enResult);
        Assert.Equal("确定", zhResult);
        Assert.Equal("OK", enResult);
    }

    // ================================================================
    // GetString — 回退行为
    // ================================================================

    /// <summary>
    /// 测试用例：请求不存在的资源键时，回退到默认语言（zh-CN）。
    /// 验证资源回退链：en → neutral/zh-CN。
    /// 对应 Postman 场景：GET /api/localization/string?key=NonExistentKey&lang=en-US
    /// 预期：返回默认语言值或占位符 "[[key]]"
    /// </summary>
    [Fact]
    public void GetString_MissingKey_ReturnsFallback()
    {
        // Arrange — 切换到英文
        _service.SetLanguage("en-US");

        // Act
        var result = _service.GetString("This.Key.Does.Not.Exist");

        // Assert — 应返回占位符
        Assert.NotNull(result);
        Assert.StartsWith("[[", result);
        Assert.EndsWith("]]", result);
    }

    /// <summary>
    /// 测试用例：空键或 null 键的边界处理。
    /// 验证服务不会因为空键而崩溃。
    /// 对应 Postman 场景：GET /api/localization/string?key=&lang=zh-CN
    /// </summary>
    [Fact]
    public void GetString_NullOrEmptyKey_ReturnsEmpty()
    {
        // Act
        var nullResult = _service.GetString(null!);
        var emptyResult = _service.GetString("");

        // Assert
        Assert.Equal(string.Empty, nullResult);
        Assert.Equal(string.Empty, emptyResult);
    }

    // ================================================================
    // SetLanguage — 语言切换
    // ================================================================

    /// <summary>
    /// 测试用例：运行时切换语言触发 LanguageChanged 事件。
    /// 验证事件参数包含正确的新旧语言代码。
    /// 对应 Postman 场景：POST /api/localization/language { "cultureCode": "en-US" }
    /// 预期：响应 200，事件触发
    /// </summary>
    [Fact]
    public void SetLanguage_ValidCode_TriggersLanguageChangedEvent()
    {
        // Arrange
        LanguageChangedEventArgs? capturedArgs = null;
        _service.LanguageChanged += (_, args) => capturedArgs = args;

        // Act
        _service.SetLanguage("en-US");

        // Assert
        Assert.NotNull(capturedArgs);
        Assert.Equal("zh-CN", capturedArgs!.OldLanguage);
        Assert.Equal("en-US", capturedArgs.NewLanguage);
        Assert.Equal("en-US", _service.CurrentLanguage);
    }

    /// <summary>
    /// 测试用例：切换到不支持的语言代码时，静默忽略并保持当前语言。
    /// 验证无效代码不会破坏服务状态。
    /// 对应 Postman 场景：POST /api/localization/language { "cultureCode": "ja-JP" }
    /// 预期：400 Bad Request 或静默忽略
    /// </summary>
    [Fact]
    public void SetLanguage_InvalidCode_KeepsCurrentLanguage()
    {
        // Arrange
        var eventFired = false;
        _service.LanguageChanged += (_, _) => eventFired = true;
        var originalLanguage = _service.CurrentLanguage;

        // Act
        _service.SetLanguage("ja-JP");

        // Assert
        Assert.False(eventFired, "不支持的语言不应触发变更事件");
        Assert.Equal(originalLanguage, _service.CurrentLanguage);
    }

    /// <summary>
    /// 测试用例：切换到当前已激活的语言时，不触发重复事件。
    /// 防止不必要的 UI 刷新。
    /// 对应 Postman 场景：重复 POST /api/localization/language { "cultureCode": "zh-CN" }
    /// </summary>
    [Fact]
    public void SetLanguage_SameLanguage_NoEventFired()
    {
        // Arrange
        var eventFired = false;
        _service.LanguageChanged += (_, _) => eventFired = true;

        // Act
        _service.SetLanguage("zh-CN"); // 当前已是 zh-CN

        // Assert
        Assert.False(eventFired, "切换到相同语言不应触发事件");
    }

    // ================================================================
    // 属性验证
    // ================================================================

    /// <summary>
    /// 测试用例：验证支持的语言列表。
    /// 对应 Postman 场景：GET /api/localization/supported-languages
    /// 预期：["zh-CN", "en-US"]
    /// </summary>
    [Fact]
    public void SupportedLanguages_ReturnsConfiguredList()
    {
        // Act
        var languages = _service.SupportedLanguages;

        // Assert
        Assert.NotNull(languages);
        Assert.Equal(2, languages.Count);
        Assert.Contains("zh-CN", languages);
        Assert.Contains("en-US", languages);
    }

    /// <summary>
    /// 测试用例：初始语言与配置服务中的语言设置一致。
    /// </summary>
    [Fact]
    public void CurrentLanguage_OnInit_MatchesConfig()
    {
        // Arrange
        var logger = NullLoggerFactory.Instance.CreateLogger<LocalizationService>();
        var config = new StubConfigService();
        config.SetLanguage("en-US");

        // Act
        using var service = new LocalizationService(logger, config);

        // Assert
        Assert.Equal("en-US", service.CurrentLanguage);
    }

    // ================================================================
    // GetString — 指定语言的重载
    // ================================================================

    /// <summary>
    /// 测试用例：使用 GetString(key, cultureCode) 重载获取指定语言的文本。
    /// 验证重载方法独立于当前语言设置。
    /// </summary>
    [Fact]
    public void GetString_WithCultureCode_UsesSpecifiedLanguage()
    {
        // Act — 当前语言为 zh-CN，但显式请求英文
        var result = _service.GetString("Status.Ready", "en-US");

        // Assert
        Assert.Equal("Ready", result);
    }

    /// <summary>
    /// 测试用例：大量资源键的全面验证 —— 确保所有支持的语言都返回非空结果。
    /// 对应 Postman 集合运行器：遍历所有键×语言组合。
    /// </summary>
    [Theory]
    [InlineData("Dashboard.SafeClean")]
    [InlineData("Dashboard.SuggestedClean")]
    [InlineData("Dashboard.ManualReview")]
    [InlineData("Dashboard.Protected")]
    [InlineData("ActionBar.OneClickClean")]
    [InlineData("ActionBar.DryRun")]
    [InlineData("Common.OK")]
    [InlineData("Common.Cancel")]
    [InlineData("Status.Ready")]
    [InlineData("Status.ReadOnlyMode")]
    [InlineData("Error.EverythingNotRunning")]
    [InlineData("Warning.IndexingIncomplete")]
    [InlineData("Settings.Tab.General")]
    [InlineData("Wizard.Welcome.Title")]
    [InlineData("Simulate.Title")]
    [InlineData("Export.Title")]
    [InlineData("Report.Summary")]
    [InlineData("App.Title")]
    public void GetString_AllLanguages_ForKey_ReturnsNonEmpty(string key)
    {
        foreach (var lang in _service.SupportedLanguages)
        {
            // Act
            var result = _service.GetString(key, lang);

            // Assert
            Assert.False(string.IsNullOrEmpty(result),
                $"键 '{key}' 在语言 {lang} 中返回了空字符串");
            Assert.False(result.Contains("[["),
                $"键 '{key}' 在语言 {lang} 中返回了占位符，说明资源缺失");
        }
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    // ================================================================
    // 测试辅助类：IConfigService 桩实现
    // ================================================================

    /// <summary>
    /// IConfigService 的轻量级桩，用于单元测试。
    /// 仅实现测试所需的最小行为，避免依赖真实的文件系统和 JSON 序列化。
    /// </summary>
    private sealed class StubConfigService : IConfigService
    {
        private readonly UserConfig _config = new();

        public UserConfig Current => _config;
        public string ConfigFilePath => @"C:\test\config.json";
        public event EventHandler? ConfigChanged;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default)
        {
            OnConfigChanged();
            return Task.CompletedTask;
        }

        public void IncrementRuleCacheVersion()
        {
            _config.RuleCacheVersion++;
        }

        /// <summary>测试辅助方法：设置语言</summary>
        public void SetLanguage(string language)
        {
            _config.Language = language;
        }

        private void OnConfigChanged()
        {
            try { ConfigChanged?.Invoke(this, EventArgs.Empty); }
            catch { /* 测试桩忽略事件处理器异常 */ }
        }
    }
}
