using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Core.Localization;

/// <summary>
/// SemanticLabelLocalizer 单元测试。
/// 验证语义标签的枚举→本地化文本映射、语言切换、以及字符串标签的反向解析。
/// 覆盖设计文档 3.3 节定义的 8 个语义标签。
/// </summary>
public class SemanticLabelLocalizerTests : IDisposable
{
    private readonly ILogger<LocalizationService> _locLogger;
    private readonly ILogger<SemanticLabelLocalizer> _labelLogger;
    private readonly StubConfigService _configService;
    private readonly LocalizationService _localizationService;
    private readonly SemanticLabelLocalizer _localizer;

    public SemanticLabelLocalizerTests()
    {
        _locLogger = NullLoggerFactory.Instance.CreateLogger<LocalizationService>();
        _labelLogger = NullLoggerFactory.Instance.CreateLogger<SemanticLabelLocalizer>();
        _configService = new StubConfigService();
        _localizationService = new LocalizationService(_locLogger, _configService);
        _localizer = new SemanticLabelLocalizer(_labelLogger, _localizationService);
    }

    // ================================================================
    // Localize(SemanticCategory) — 枚举 → 文本
    // ================================================================

    /// <summary>
    /// 测试用例：验证所有 8 个 SemanticCategory 枚举值在中文下都有对应的非空文本。
    /// 确保映射表完整性，防止新增枚举值后遗漏映射。
    /// </summary>
    [Fact]
    public void Localize_EachCategory_Chinese_ReturnsNonEmptyString()
    {
        // Arrange — 默认语言为 zh-CN
        var categories = Enum.GetValues<SemanticCategory>();

        foreach (var category in categories)
        {
            // Act
            var result = _localizer.Localize(category);

            // Assert
            Assert.False(string.IsNullOrEmpty(result),
                $"语义分类 {category} 在中文下返回了空字符串");
            Assert.False(result.Contains("[["),
                $"语义分类 {category} 在中文下返回了占位符");
        }
    }

    /// <summary>
    /// 测试用例：验证所有 8 个 SemanticCategory 枚举值在英文下都有对应的非空文本。
    /// </summary>
    [Fact]
    public void Localize_EachCategory_English_ReturnsNonEmptyString()
    {
        // Arrange — 切换到英文
        _localizationService.SetLanguage("en-US");

        var categories = Enum.GetValues<SemanticCategory>();
        foreach (var category in categories)
        {
            // Act
            var result = _localizer.Localize(category);

            // Assert
            Assert.False(string.IsNullOrEmpty(result),
                $"语义分类 {category} 在英文下返回了空字符串");
            Assert.False(result.Contains("[["),
                $"语义分类 {category} 在英文下返回了占位符");
        }
    }

    // ================================================================
    // 语言切换
    // ================================================================

    /// <summary>
    /// 测试用例：语言切换后，同一个枚举值返回不同语言的文本。
    /// 验证 Localize 正确响应语言变更事件。
    /// </summary>
    [Fact]
    public void Localize_SwitchLanguage_ReturnsDifferentText()
    {
        // Act — 中文
        var zhResult = _localizer.Localize(SemanticCategory.SystemTempFiles);

        // 切换语言
        _localizationService.SetLanguage("en-US");
        var enResult = _localizer.Localize(SemanticCategory.SystemTempFiles);

        // Assert
        Assert.Equal("系统临时文件", zhResult);
        Assert.Equal("System Temporary Files", enResult);
        Assert.NotEqual(zhResult, enResult);
    }

    /// <summary>
    /// 测试用例：使用指定语言的重载不依赖当前语言状态。
    /// </summary>
    [Fact]
    public void Localize_WithCultureCode_IndependentOfCurrentLanguage()
    {
        // Arrange — 当前语言为 zh-CN

        // Act — 显式请求英文
        var enResult = _localizer.Localize(SemanticCategory.AppCache, "en-US");
        var zhResult = _localizer.Localize(SemanticCategory.AppCache, "zh-CN");

        // Assert
        Assert.Equal("Application Cache", enResult);
        Assert.Equal("应用缓存", zhResult);
    }

    // ================================================================
    // LocalizeFromString — 字符串 → 本地化文本
    // ================================================================

    /// <summary>
    /// 测试用例：从中文标签字符串反向解析并本地化。
    /// 验证与 FileDecisionCache.SemanticCategory（string 类型）的兼容性。
    /// </summary>
    [Fact]
    public void LocalizeFromString_ChineseLabel_ReturnsLocalizedText()
    {
        // Act
        var zhResult = _localizer.LocalizeFromString("系统临时文件");

        _localizationService.SetLanguage("en-US");
        var enResult = _localizer.LocalizeFromString("系统临时文件");

        // Assert
        Assert.Equal("系统临时文件", zhResult);
        Assert.Equal("System Temporary Files", enResult);
    }

    /// <summary>
    /// 测试用例：从英文标签字符串反向解析。
    /// 兼容 AI 返回英文标签的场景。
    /// </summary>
    [Fact]
    public void LocalizeFromString_EnglishLabel_ReturnsLocalizedText()
    {
        // Act — 当前语言中文
        var zhResult = _localizer.LocalizeFromString("Old Downloads");

        _localizationService.SetLanguage("en-US");
        var enResult = _localizer.LocalizeFromString("Old Downloads");

        // Assert
        Assert.Equal("下载旧文件", zhResult);
        Assert.Equal("Old Downloads", enResult);
    }

    /// <summary>
    /// 测试用例：未知标签字符串按原样返回。
    /// 确保自定义规则标签不会因解析失败而丢失。
    /// </summary>
    [Fact]
    public void LocalizeFromString_UnknownLabel_ReturnsOriginalString()
    {
        // Arrange
        const string customLabel = "自定义规则标签";

        // Act
        var result = _localizer.LocalizeFromString(customLabel);

        // Assert
        Assert.Equal(customLabel, result);
    }

    /// <summary>
    /// 测试用例：空字符串和 null 的安全处理。
    /// </summary>
    [Fact]
    public void LocalizeFromString_NullOrEmpty_ReturnsEmpty()
    {
        // Act & Assert
        Assert.Equal(string.Empty, _localizer.LocalizeFromString(null!));
        Assert.Equal(string.Empty, _localizer.LocalizeFromString(""));
    }

    // ================================================================
    // 枚举名称反查（容错）
    // ================================================================

    /// <summary>
    /// 测试用例：按枚举名称（如 "SystemTempFiles"）反向查找。
    /// 验证容错路径：当标签文本不匹配时，回退到枚举名称解析。
    /// </summary>
    [Fact]
    public void LocalizeFromString_ByEnumName_ReturnsCorrectLocalizedText()
    {
        // Act
        var result = _localizer.LocalizeFromString("SystemTempFiles");

        // Assert
        Assert.Equal("系统临时文件", result);
    }

    // ================================================================
    // GetAllLabels — 批量获取
    // ================================================================

    /// <summary>
    /// 测试用例：验证 GetAllLabels 返回全部 8 个语义标签的映射。
    /// 用于 UI 批量渲染场景。
    /// </summary>
    [Fact]
    public void GetAllLabels_ReturnsAllEightCategories()
    {
        // Act
        var labels = _localizer.GetAllLabels();

        // Assert
        Assert.NotNull(labels);
        Assert.Equal(8, labels.Count);

        // 验证所有枚举值都有对应的本地化文本
        foreach (var category in Enum.GetValues<SemanticCategory>())
        {
            Assert.True(labels.ContainsKey(category),
                $"GetAllLabels 缺少分类: {category}");
            Assert.False(string.IsNullOrEmpty(labels[category]),
                $"GetAllLabels 中 {category} 的文本为空");
        }
    }

    /// <summary>
    /// 测试用例：验证原始映射表的完整性 —— 确保 8 个分类都有中英文映射。
    /// </summary>
    [Fact]
    public void RawLabelMap_AllCategoriesHaveBothLanguages()
    {
        // Act — 通过 internal 方法获取原始映射表
        var rawMap = SemanticLabelLocalizer.GetRawLabelMap();

        // Assert
        Assert.Equal(8, rawMap.Count);

        foreach (var kv in rawMap)
        {
            Assert.False(string.IsNullOrEmpty(kv.Value.Zh),
                $"分类 {kv.Key} 缺少中文标签");
            Assert.False(string.IsNullOrEmpty(kv.Value.En),
                $"分类 {kv.Key} 缺少英文标签");
            Assert.True(kv.Value.Zh != kv.Value.En,
                $"分类 {kv.Key} 的中英文标签不应相同");
        }
    }

    public void Dispose()
    {
        _localizer.Dispose();
        _localizationService.Dispose();
    }

    // ================================================================
    // 测试辅助类（与 LocalizationServiceTests 共享相同的桩实现）
    // ================================================================

    private sealed class StubConfigService : DirectoryCleanAgent.Core.Config.IConfigService
    {
        private readonly UserConfig _config = new();

        public UserConfig Current => _config;
        public string ConfigFilePath => @"C:\test\config.json";
        public event EventHandler? ConfigChanged;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default)
        {
            try { ConfigChanged?.Invoke(this, EventArgs.Empty); }
            catch { }
            return Task.CompletedTask;
        }

        public void IncrementRuleCacheVersion()
        {
            _config.RuleCacheVersion++;
        }
    }
}
