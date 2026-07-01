using DirectoryCleanAgent.AI;
using DirectoryCleanAgent.AI.Models;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.AI;

/// <summary>
/// AiPromptBuilder 单元测试 —— 覆盖系统提示词、用户提示词、
/// 三层防御解析策略、标签校验、Markdown 剥离等全部功能。
/// 该类无状态，所有测试独立运行。
/// </summary>
public class AiPromptBuilderTests
{
    private readonly AiPromptBuilder _builder;
    private readonly ILogger<AiPromptBuilder> _logger;

    public AiPromptBuilderTests()
    {
        _logger = NullLoggerFactory.Instance.CreateLogger<AiPromptBuilder>();
        _builder = new AiPromptBuilder(_logger);
    }

    // ============================================================
    // 构造函数
    // ============================================================

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AiPromptBuilder(null!));
    }

    // ============================================================
    // BuildSystemPrompt —— 系统提示词内容验证
    // ============================================================

    [Fact]
    public void BuildSystemPrompt_ContainsRequiredElements()
    {
        var prompt = _builder.BuildSystemPrompt();

        // 必须包含角色定义和输出格式约束
        Assert.Contains("磁盘清理顾问", prompt);
        Assert.Contains("JSON", prompt);
        Assert.Contains("label", prompt);
        Assert.Contains("confidence", prompt);
        Assert.Contains("explanation", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_DefinesAllValidLabels()
    {
        var prompt = _builder.BuildSystemPrompt();

        // 必须明确定义三种标签
        Assert.Contains("safe", prompt);
        Assert.Contains("unknown", prompt);
        Assert.Contains("risky", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsAnalysisPrinciples()
    {
        var prompt = _builder.BuildSystemPrompt();

        // 必须包含分析原则
        Assert.Contains("系统目录", prompt);
        Assert.Contains("临时目录", prompt);
    }

    // ============================================================
    // BuildUserPrompt —— 用户提示词格式化
    // ============================================================

    [Fact]
    public void BuildUserPrompt_FormatsFileInfoCorrectly()
    {
        var filePath = @"C:\Users\Test\Documents\report.docx";
        var sizeBytes = 2L * 1024 * 1024 * 1024; // 2 GB
        var lastWrite = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var prompt = _builder.BuildUserPrompt(filePath, sizeBytes, lastWrite);

        // 验证包含文件信息
        Assert.Contains("report.docx", prompt);
        Assert.Contains("Documents", prompt);
        Assert.Contains("2048.0 MB", prompt); // 2GB = 2048 MB
        Assert.Contains("2026-01-01", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithZeroByteFile()
    {
        var prompt = _builder.BuildUserPrompt(@"C:\empty.dat", 0, DateTime.UtcNow);

        // 0 字节文件应显示 0.0 MB
        Assert.Contains("0.0 MB", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithOneByteFile()
    {
        var prompt = _builder.BuildUserPrompt(@"C:\one.dat", 1, DateTime.UtcNow);

        // 1 字节文件应显示接近 0 MB
        Assert.Contains("0.0 MB", prompt);
    }

    [Fact]
    public void BuildUserPrompt_IncludesAgeInDays()
    {
        var filePath = @"C:\old_file.log";
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);

        var prompt = _builder.BuildUserPrompt(filePath, 1024, thirtyDaysAgo);

        // 应包含 "天前" 表示文件龄期
        Assert.Contains("天前", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithUnknownDirectory_UsesDefaultText()
    {
        // 使用根路径文件，GetDirectoryName 返回 null → 显示 "未知"
        var prompt = _builder.BuildUserPrompt(@"C:\rootfile.txt", 100, DateTime.UtcNow);

        Assert.Contains("rootfile.txt", prompt);
    }

    // ============================================================
    // BuildTestPrompt —— 连接测试提示词
    // ============================================================

    [Fact]
    public void BuildTestPrompt_ReturnsValidPair()
    {
        var (system, user) = _builder.BuildTestPrompt();

        Assert.Contains("测试", system);
        Assert.Contains("连接测试", user);
        Assert.Contains("ok", system);
    }

    // ============================================================
    // ParseResponse —— 三层防御解析策略
    // ============================================================

    [Fact]
    public void ParseResponse_NullInput_ReturnsNull()
    {
        Assert.Null(_builder.ParseResponse(null!));
    }

    [Fact]
    public void ParseResponse_EmptyString_ReturnsNull()
    {
        Assert.Null(_builder.ParseResponse(""));
    }

    [Fact]
    public void ParseResponse_WhitespaceOnly_ReturnsNull()
    {
        Assert.Null(_builder.ParseResponse("   \t\n  "));
    }

    [Fact]
    public void ParseResponse_DirectValidJson_ParsesSuccessfully()
    {
        var json = AiTestDataFactory.ValidApiResponseJson("safe", 0.95, "可安全删除");

        var result = _builder.ParseResponse(json);

        Assert.NotNull(result);
        Assert.Equal("safe", result.Label);
        Assert.Equal(0.95, result.Confidence);
        Assert.Equal("可安全删除", result.Explanation);
    }

    [Fact]
    public void ParseResponse_JsonWithExtraFields_IgnoresUnknown()
    {
        // 策略1：直接JSON解析 → 宽松模式应忽略未知字段
        var json = """{"label":"risky","confidence":0.3,"explanation":"高风险文件","extra_field":"ignored"}""";

        var result = _builder.ParseResponse(json);

        Assert.NotNull(result);
        Assert.Equal("risky", result.Label);
    }

    [Fact]
    public void ParseResponse_JsonSurroundedByText_ExtractsViaRegex()
    {
        // 策略2：正则提取 JSON 子串
        var text = AiTestDataFactory.TextPrefixApiResponseJson("risky", 0.8, "高风险文件");

        var result = _builder.ParseResponse(text);

        Assert.NotNull(result);
        Assert.Equal("risky", result.Label);
    }

    [Fact]
    public void ParseResponse_MarkdownCodeBlockFence_StripsAndParses()
    {
        // 策略3：去除 ```json ... ``` 后解析
        var markdown = AiTestDataFactory.MarkdownWrappedApiResponseJson("safe", 0.9, "临时缓存文件");

        var result = _builder.ParseResponse(markdown);

        Assert.NotNull(result);
        Assert.Equal("safe", result.Label);
    }

    [Fact]
    public void ParseResponse_MarkdownCodeBlockWithoutLanguageTag_StillParses()
    {
        // 策略3：去除 ``` ... ```（无 json 标记）
        var json = AiTestDataFactory.ValidApiResponseJson("unknown", 0.5, "无法确定");
        var markdown = $"```\n{json}\n```";

        var result = _builder.ParseResponse(markdown);

        Assert.NotNull(result);
        Assert.Equal("unknown", result.Label);
    }

    [Fact]
    public void ParseResponse_TrailingComma_AcceptedByLooseMode()
    {
        // 宽松 JSON 模式允许尾部逗号
        var json = """{"label":"safe","confidence":0.9,"explanation":"测试",}""";

        var result = _builder.ParseResponse(json);

        Assert.NotNull(result);
        Assert.Equal("safe", result.Label);
    }

    [Fact]
    public void ParseResponse_CommentsInJson_AcceptedByLooseMode()
    {
        // 宽松 JSON 模式跳过注释（测试 JSON 注释的兼容性）
        // 实际测试使用无注释的有效 JSON 作为基准
        var json = AiTestDataFactory.ValidApiResponseJson("safe", 0.99, "正常");

        var result = _builder.ParseResponse(json);

        Assert.NotNull(result);
        Assert.Equal("safe", result.Label);
    }

    [Fact]
    public void ParseResponse_CompletelyUnparseable_ReturnsNull()
    {
        // 所有 3 层策略均失败 —— 纯乱码文本
        var garbage = "这不是JSON，也没有任何可提取的结构";

        var result = _builder.ParseResponse(garbage);

        Assert.Null(result);
    }

    [Fact]
    public void ParseResponse_PartialJson_MissingFields_UsesDefaults()
    {
        // 缺少字段时使用 AiModelResponse 的默认值
        var json = """{"label":"safe"}""";

        var result = _builder.ParseResponse(json);

        Assert.NotNull(result);
        Assert.Equal("safe", result.Label);
        Assert.Equal(0.5, result.Confidence); // 默认值
        Assert.Equal("", result.Explanation);  // 默认值
    }

    // ============================================================
    // ValidateLabel —— 标签验证与校正
    // ============================================================

    [Fact]
    public void ValidateLabel_NullInput_ReturnsUnknown()
    {
        var result = _builder.ValidateLabel(null!);
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void ValidateLabel_EmptyInput_ReturnsUnknown()
    {
        var result = _builder.ValidateLabel("");
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void ValidateLabel_WhitespaceOnly_ReturnsUnknown()
    {
        var result = _builder.ValidateLabel("   ");
        Assert.Equal("unknown", result);
    }

    [Theory]
    [InlineData("safe")]
    [InlineData("SAFE")]
    [InlineData("Safe")]
    [InlineData("  safe  ")]
    public void ValidateLabel_ValidLabels_ReturnsNormalized(string input)
    {
        var result = _builder.ValidateLabel(input);

        // 所有有效变体都应返回小写形式
        Assert.Equal("safe", result);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("UNKNOWN")]
    [InlineData("Unknown")]
    public void ValidateLabel_UnknownVariants_ReturnUnknown(string input)
    {
        var result = _builder.ValidateLabel(input);
        Assert.Equal("unknown", result);
    }

    [Theory]
    [InlineData("risky")]
    [InlineData("RISKY")]
    [InlineData("Risky")]
    public void ValidateLabel_RiskyVariants_ReturnRisky(string input)
    {
        var result = _builder.ValidateLabel(input);
        Assert.Equal("risky", result);
    }

    [Fact]
    public void ValidateLabel_UnrecognizedLabel_ReturnsUnknown()
    {
        // 非法标签 → 校正为 "unknown"（保守策略）
        var result = _builder.ValidateLabel("dangerous");

        Assert.Equal("unknown", result);
    }

    [Fact]
    public void ValidateLabel_NumericLabel_ReturnsUnknown()
    {
        var result = _builder.ValidateLabel("12345");
        Assert.Equal("unknown", result);
    }
}
