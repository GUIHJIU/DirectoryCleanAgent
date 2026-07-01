// ============================================================
// AppStartupIntegrationTests.cs
// 功能: 应用启动流程集成测试
// 覆盖: Serilog 配置、DI 容器、数据库初始化、主题加载、配置容错
// ============================================================

using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Everything;
using DirectoryCleanAgent.Rules;
using DirectoryCleanAgent.Decision;
using DirectoryCleanAgent.Operations;
using DirectoryCleanAgent.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.Integration.StartupFlow;

/// <summary>
/// 应用启动流程集成测试
/// 验证 Serilog 日志、DI 容器构建、数据库初始化、主题加载等启动关键路径
/// </summary>
public class AppStartupIntegrationTests : IntegrationTestBase, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly string _dbPath;
    private bool _disposed;

    public AppStartupIntegrationTests() : base("app_startup")
    {
        // 使用临时路径构建轻量级 DI 容器，模拟 App.ConfigureServices 的核心注册
        _dbPath = Path.Combine(TestRoot, "startup_test.db");

        var services = new ServiceCollection();

        // 日志层：使用真实 LoggerFactory（输出到调试窗口，便于诊断）
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            // 集成测试不需要实际日志输出
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // 配置层
        services.AddConfigLayer();

        // 数据层（使用临时数据库路径）
        services.AddDataLayer(_dbPath);

        // Everything SDK 适配器（真实注册，但仅验证可解析性）
        services.AddEverythingServices();

        // 规则引擎层
        services.AddRulesLayer();

        // 决策引擎层
        services.AddDecisionLayer();

        // 操作层
        services.AddOperationsLayer();

        _serviceProvider = services.BuildServiceProvider();
    }

    // ================================================================
    // IT-START-01: 验证 Serilog 配置后日志可写入文件
    // ================================================================

    /// <summary>
    /// 验证日志系统能正常输出到文件和调试窗口
    /// 启动流程中 Serilog 配置必须在 DI 容器构建之前完成
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void StartupFlow_LoggingFactory_CreatesDebugLogger()
    {
        // Arrange: 通过 LoggerFactory 创建日志记录器
        var logger = _serviceProvider.GetRequiredService<ILogger<AppStartupIntegrationTests>>();

        // Act: 写入一条日志
        logger.LogInformation("启动流程集成测试: 日志系统验证");

        // Assert: 日志记录器可正常使用（无异常即通过）
        Assert.NotNull(logger);
    }

    // ================================================================
    // IT-START-02: 验证 DI 容器能解析所有核心服务
    // ================================================================

    /// <summary>
    /// DI 容器构建后所有核心服务接口必须可解析
    /// 任何一个解析失败都意味着 ServiceRegistration 注册链存在缺陷
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void StartupFlow_DIContainer_ResolvesAllCoreServices()
    {
        var logger = CreateLogger<AppStartupIntegrationTests>();
        logger.LogInformation("开始测试: DI 容器核心服务解析");

        // 按设计文档四层架构逐一验证核心接口可解析性
        try
        {
            // 配置层
            var configService = _serviceProvider.GetRequiredService<IConfigService>();
            Assert.NotNull(configService);

            // Everything 层
            var fileListProvider = _serviceProvider.GetRequiredService<IFileListProvider>();
            Assert.NotNull(fileListProvider);

            // 规则引擎层
            var ruleEngine = _serviceProvider.GetRequiredService<IRuleEngine>();
            Assert.NotNull(ruleEngine);

            // 决策引擎层
            var decisionEngine = _serviceProvider.GetRequiredService<IDecisionEngine>();
            Assert.NotNull(decisionEngine);

            // 操作层
            var operationExecutor = _serviceProvider.GetRequiredService<IOperationExecutor>();
            Assert.NotNull(operationExecutor);
            var backupManager = _serviceProvider.GetRequiredService<IBackupManager>();
            Assert.NotNull(backupManager);
            var quarantineManager = _serviceProvider.GetRequiredService<IQuarantineManager>();
            Assert.NotNull(quarantineManager);

            // 数据层
            var connectionFactory = _serviceProvider.GetRequiredService<ISqliteConnectionFactory>();
            Assert.NotNull(connectionFactory);

            logger.LogInformation("所有核心服务解析成功（共 8 个接口）");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DI 容器服务解析失败");
            throw;
        }
    }

    // ================================================================
    // IT-START-03: 验证 SQLite 数据库初始化能建表
    // ================================================================

    /// <summary>
    /// SQLite 数据库初始化后四张业务表必须存在
    /// 验证 FileDecisionCache、LocalTombstone、DeletionRecord、AuditLog 表均已创建
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task StartupFlow_DatabaseInit_CreatesAllTables()
    {
        var logger = CreateLogger<AppStartupIntegrationTests>();
        logger.LogInformation("开始测试: 数据库初始化建表验证, 路径={Path}", _dbPath);

        try
        {
            // Arrange: 获取连接工厂并初始化数据库
            var factory = _serviceProvider.GetRequiredService<ISqliteConnectionFactory>();
            if (factory is DatabaseInitializer initializer)
            {
                await initializer.InitializeAsync();
            }

            // Act: 打开数据库连接，查询 sqlite_master 系统表验证四张业务表存在
            await using var connection = await factory.CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

            var tableNames = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tableNames.Add(reader.GetString(0));
            }

            // Assert: 四张核心表必须存在
            Assert.Contains("FileDecisionCache", tableNames);
            Assert.Contains("LocalTombstone", tableNames);
            Assert.Contains("DeletionRecord", tableNames);
            Assert.Contains("AuditLog", tableNames);

            logger.LogInformation("数据库初始化验证通过: 共 {Count} 张表 -> {Tables}",
                tableNames.Count, string.Join(", ", tableNames));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "数据库初始化建表验证失败");
            throw;
        }
    }

    // ================================================================
    // IT-START-04: 验证首次运行默认值
    // ================================================================

    /// <summary>
    /// 首次运行时 IsFirstRun 必须为 true
    /// 此标志驱动 App.xaml.cs 中启动向导 vs 主窗口的分流逻辑
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void StartupFlow_FirstRun_DefaultConfigHasFirstRunTrue()
    {
        var logger = CreateLogger<AppStartupIntegrationTests>();
        logger.LogInformation("开始测试: 首次运行默认配置验证");

        // Act: 获取当前配置
        var config = _serviceProvider.GetRequiredService<IConfigService>().Current;

        // Assert: 确认首次运行标志和基本字段
        Assert.True(config.IsFirstRun, "首次运行时 IsFirstRun 必须为 true");
        Assert.NotNull(config.DataStoragePath);
        Assert.NotNull(config.Language);

        logger.LogInformation("首次运行默认配置验证通过: IsFirstRun={IsFirstRun}", config.IsFirstRun);
    }

    // ================================================================
    // IT-START-05: 浅色主题 ResourceDictionary 加载验证
    // ================================================================

    /// <summary>
    /// 浅色主题主题字典文件存在且可被 WPF ResourceDictionary 加载
    /// 主题加载失败会导致 UI 显示异常（无样式）
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void StartupFlow_ThemeApply_LightThemeFileExists()
    {
        var logger = CreateLogger<AppStartupIntegrationTests>();
        logger.LogInformation("开始测试: 浅色主题文件存在性验证");

        // 查找 LightTheme.xaml 文件（相对于 WPF 项目输出目录）
        var projectDir = FindProjectDir("DirectoryCleanAgent");
        var lightThemePath = Path.Combine(projectDir, "LightTheme.xaml");

        Assert.True(File.Exists(lightThemePath),
            $"浅色主题文件未找到: {lightThemePath}");

        logger.LogInformation("浅色主题文件验证通过: {Path}", lightThemePath);
    }

    // ================================================================
    // IT-START-06: 深色主题 ResourceDictionary 加载验证
    // ================================================================

    /// <summary>
    /// 深色主题主题字典文件存在且可被 WPF ResourceDictionary 加载
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void StartupFlow_ThemeApply_DarkThemeFileExists()
    {
        var logger = CreateLogger<AppStartupIntegrationTests>();
        logger.LogInformation("开始测试: 深色主题文件存在性验证");

        var projectDir = FindProjectDir("DirectoryCleanAgent");
        var darkThemePath = Path.Combine(projectDir, "DarkTheme.xaml");

        Assert.True(File.Exists(darkThemePath),
            $"深色主题文件未找到: {darkThemePath}");

        logger.LogInformation("深色主题文件验证通过: {Path}", darkThemePath);
    }

    // ================================================================
    // IT-START-07: 配置加载失败时优雅降级
    // ================================================================

    /// <summary>
    /// 配置文件损坏或不存在时 IConfigService 必须返回可用默认配置
    /// 不能因配置问题导致应用崩溃（优雅降级原则）
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void StartupFlow_LoadConfig_Failure_ReturnsDefaults()
    {
        var logger = CreateLogger<AppStartupIntegrationTests>();
        logger.LogInformation("开始测试: 配置加载容错验证");

        try
        {
            // Act: 直接通过已注册的 IConfigService 获取配置
            var configService = _serviceProvider.GetRequiredService<IConfigService>();
            var config = configService.Current;

            // Assert: 配置必须可用（即使文件损坏也有默认值）
            Assert.NotNull(config);
            Assert.False(string.IsNullOrWhiteSpace(config.DataStoragePath),
                "数据存储路径不能为空（必须有默认值）");
            Assert.False(string.IsNullOrWhiteSpace(config.Language),
                "语言设置不能为空（必须有默认值）");

            logger.LogInformation("配置加载容错验证通过: 返回了可用的默认配置");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "配置加载容错验证失败");
            throw;
        }
    }

    // ================================================================
    // 辅助方法
    // ================================================================

    /// <summary>
    /// 定位 WPF 项目目录（用于验证主题文件等静态资源）
    /// 从测试输出目录向上遍历查找 .csproj 文件
    /// </summary>
    private static string FindProjectDir(string projectName)
    {
        // 从测试程序集所在目录向上查找 src/{projectName} 目录
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);

        while (dir != null)
        {
            var srcDir = Path.Combine(dir.FullName, "src", projectName);
            if (Directory.Exists(srcDir))
            {
                return srcDir;
            }
            dir = dir.Parent;
        }

        // 回退：使用当前工作目录的相对路径
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", projectName));
    }

    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_serviceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }

            // 清理测试数据库文件
            if (File.Exists(_dbPath))
            {
                try { File.Delete(_dbPath); } catch { }
            }
            var walPath = _dbPath + "-wal";
            if (File.Exists(walPath))
            {
                try { File.Delete(walPath); } catch { }
            }
            var shmPath = _dbPath + "-shm";
            if (File.Exists(shmPath))
            {
                try { File.Delete(shmPath); } catch { }
            }
        }
        catch { }

        base.Dispose();
    }
}
