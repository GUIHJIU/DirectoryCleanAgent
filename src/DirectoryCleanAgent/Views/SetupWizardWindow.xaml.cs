using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.AI;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.ViewModels;

namespace DirectoryCleanAgent.Views;

/// <summary>
/// 首次启动配置向导窗口 — 引导用户完成 Everything 检测和基本配置。
///
/// 使用 ContentControl 动态切换步骤视图，每个步骤的 UI 在代码后台构建。
/// ViewModel 为 SetupWizardViewModel，负责所有业务逻辑和状态管理。
/// </summary>
public partial class SetupWizardWindow : Window
{
    private readonly ILogger<SetupWizardWindow> _logger;
    private readonly SetupWizardViewModel _viewModel;
    private readonly IAiAdvisorService _aiAdvisorService;
    private bool _isCompleting; // 防止重复触发完成流程

    // 防止 NavigateToStep → PropertyChanged → NavigateToStep 递归
    private bool _isNavigating;

    // Step0 动态 UI 控件引用
    private TextBlock? _step0StatusText;
    private ProgressBar? _step0ProgressBar;
    private StackPanel? _step0DetectionPanel;
    private StackPanel? _step0ErrorPanel;
    private StackPanel? _step0IndexWaitPanel;
    private StackPanel? _step0WaitingPanel;
    private TextBlock? _step0WaitElapsedText;
    private StackPanel? _step0InstallPanel;
    private StackPanel? _step0InstallProgressPanel;
    private TextBlock? _step0InstallStatusText;
    private ProgressBar? _step0InstallProgressBar;

    // Step4 AI 测试连接状态
    private TextBlock? _aiTestResultText;

    public SetupWizardWindow(
        SetupWizardViewModel viewModel,
        IAiAdvisorService aiAdvisorService,
        ILogger<SetupWizardWindow> logger)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _aiAdvisorService = aiAdvisorService ?? throw new ArgumentNullException(nameof(aiAdvisorService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        try
        {
            InitializeComponent();
            DataContext = _viewModel;

            // 绑定步骤指示器数据源
            StepIndicatorList.ItemsSource = _viewModel.Steps;

            // 监听 ViewModel 属性变更（用于刷新步骤导航状态）
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // 监听向导完成/跳过事件
            _viewModel.WizardCompleted += OnWizardCompleted;
            _viewModel.WizardSkipped += OnWizardSkipped;

            _logger.LogInformation("SetupWizardWindow 初始化完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetupWizardWindow 初始化失败");
            MessageBox.Show($"向导窗口初始化失败: {ex.Message}", "启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    // ================================================================
    // 窗口生命周期
    // ================================================================

    /// <summary>窗口加载完成后启动 Everything 依赖检测</summary>
    private async void SetupWizardWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogMethodEntry("向导窗口已加载");
            NavigateToStep(0);
            await _viewModel.RunEverythingDetectionAsync();
            NavigateToStep(0); // 刷新 Step0 显示检测结果
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "向导窗口加载异常");
        }
    }

    /// <summary>
    /// 窗口关闭前拦截：若处于后台等待模式则隐藏窗口而非关闭；
    /// 若用户强行关闭则视为跳过向导并保存默认配置。
    /// </summary>
    private async void SetupWizardWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            if (_isCompleting)
            {
                // 正常完成流程，允许关闭
                _logger.LogInformation("向导正常完成，窗口关闭");
                return;
            }

            if (_viewModel.IsBackgroundWaiting)
            {
                // 后台等待中 — 不关闭，仅隐藏到托盘
                e.Cancel = true;
                Hide();
                _logger.LogInformation("后台等待中，窗口隐藏到托盘");
                return;
            }

            // 用户强行关闭窗口 → 视为跳过向导
            _logger.LogWarning("用户强制关闭向导窗口，使用默认配置");
            _viewModel.UserConfig.IsFirstRun = false;
            _viewModel.UserConfig.UserMode = UserMode.Simple;
            await _viewModel.SaveConfigAsync();
            OnWizardSkipped(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "向导窗口关闭处理异常");
        }
    }

    // ================================================================
    // 步骤导航
    // ================================================================

    /// <summary>导航到指定步骤并刷新 UI</summary>
    private void NavigateToStep(int stepIndex)
    {
        if (_isNavigating || stepIndex < 0 || stepIndex >= _viewModel.Steps.Count)
            return;

        _isNavigating = true;
        try
        {
            _viewModel.CurrentStepIndex = stepIndex;

        // 更新步骤指示器中的 IsActive 状态
        foreach (var step in _viewModel.Steps)
        {
            step.IsActive = step.Index == stepIndex;
        }

        // 刷新步骤指示器
        StepIndicatorList.ItemsSource = null;
        StepIndicatorList.ItemsSource = _viewModel.Steps;

        // 根据步骤索引构建相应内容
        StepContentHost.Content = BuildStepContent(stepIndex);

        // 刷新导航按钮状态
            RefreshNavigationButtons();

            _logger.LogDebug("导航到步骤 {Index}", stepIndex);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    /// <summary>
    /// 根据步骤索引构建对应的 UI 元素树。
    /// 每个步骤的 UI 在此方法中创建，绑定到 ViewModel 的对应属性。
    /// </summary>
    private UIElement BuildStepContent(int stepIndex)
    {
        return stepIndex switch
        {
            0 => BuildStep0_EverythingCheck(),
            1 => BuildStep1_Welcome(),
            2 => BuildStep2_Profile(),
            3 => BuildStep3_Exclusions(),
            4 => BuildStep4_AIConfig(),
            _ => new TextBlock { Text = "未知步骤" }
        };
    }

    // ================================================================
    // Step0: Everything 依赖检测
    // ================================================================

    private UIElement BuildStep0_EverythingCheck()
    {
        var panel = new StackPanel();

        // 标题
        panel.Children.Add(new TextBlock
        {
            Text = "Everything 搜索引擎依赖检测",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Everything 是本工具的高性能文件索引引擎，必须安装并运行才能使用。正在检测 Everything 服务状态…",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20)
        });

        // 检测中状态
        _step0DetectionPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _step0StatusText = new TextBlock
        {
            Text = "正在检测 Everything 依赖状态…",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _step0ProgressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Width = 300,
            Height = 4,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _step0DetectionPanel.Children.Add(_step0StatusText);
        _step0DetectionPanel.Children.Add(_step0ProgressBar);
        panel.Children.Add(_step0DetectionPanel);

        // 错误面板（初始隐藏）
        _step0ErrorPanel = CreateErrorPanel("检测失败", "");
        _step0ErrorPanel.Visibility = Visibility.Collapsed;
        panel.Children.Add(_step0ErrorPanel);

        // 检测结果摘要面板（初始隐藏）
        panel.Children.Add(BuildDetectionResultSummary());

        // 索引等待选项面板（初始隐藏）
        _step0IndexWaitPanel = BuildIndexWaitOptionsPanel();
        _step0IndexWaitPanel.Visibility = Visibility.Collapsed;
        panel.Children.Add(_step0IndexWaitPanel);

        // 索引等待中面板（初始隐藏）
        _step0WaitingPanel = BuildIndexWaitingPanel();
        _step0WaitingPanel.Visibility = Visibility.Collapsed;
        panel.Children.Add(_step0WaitingPanel);

        // 安装 Everything 选项面板（初始隐藏）
        _step0InstallPanel = BuildEverythingInstallPanel();
        _step0InstallPanel.Visibility = Visibility.Collapsed;
        panel.Children.Add(_step0InstallPanel);

        // 安装进度面板（初始隐藏）
        _step0InstallProgressPanel = BuildEverythingInstallProgressPanel();
        _step0InstallProgressPanel.Visibility = Visibility.Collapsed;
        panel.Children.Add(_step0InstallProgressPanel);

        // 重试/退出按钮（仅错误时显示）
        var retryPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var retryButton = new Button
        {
            Content = "重试检测",
            Width = 110,
            Height = 34,
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 10, 0)
        };
        retryButton.Click += async (s, e) =>
        {
            await _viewModel.RunEverythingDetectionAsync();
            UpdateStep0UI();
        };
        var exitButton = new Button
        {
            Content = "退出",
            Width = 80,
            Height = 34,
            FontSize = 13
        };
        exitButton.Click += (s, e) => Application.Current.Shutdown();
        retryPanel.Children.Add(retryButton);
        retryPanel.Children.Add(exitButton);

        // 通过 Visibility 绑定控制（使用 ViewModel 属性）
        UpdateStep0UI();

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
    }

    /// <summary>更新 Step0 UI 状态（根据 ViewModel 的检测状态）</summary>
    private void UpdateStep0UI()
    {
        if (_step0DetectionPanel == null) return;

        // 更新检测状态面板
        _step0DetectionPanel.Visibility = _viewModel.IsDetecting
            ? Visibility.Visible : Visibility.Collapsed;

        // 错误面板
        if (_step0ErrorPanel != null)
        {
            _step0ErrorPanel.Visibility = !string.IsNullOrEmpty(_viewModel.DetectionError)
                ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrEmpty(_viewModel.DetectionError))
            {
                var errorText = _step0ErrorPanel.FindName("ErrorTextBlock") as TextBlock;
                if (errorText != null)
                    errorText.Text = _viewModel.DetectionError;
            }
        }

        // 索引等待选项面板（检测完成 + 索引未就绪 + 未开始等待 + 未超时）
        if (_step0IndexWaitPanel != null)
        {
            _step0IndexWaitPanel.Visibility =
                _viewModel.IsDetectionComplete &&
                _viewModel.DetectionResult?.IsIndexing == true &&
                !_viewModel.IsWaitingForIndex &&
                !_viewModel.IsIndexTimeout
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        // 索引等待中面板
        if (_step0WaitingPanel != null)
        {
            _step0WaitingPanel.Visibility = _viewModel.IsWaitingForIndex
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // 更新等待时间文本
        if (_step0WaitElapsedText != null)
        {
            _step0WaitElapsedText.Text = _viewModel.WaitElapsedText;
        }

        // 安装选项面板（未安装 Everything + 未开始安装）
        if (_step0InstallPanel != null)
        {
            _step0InstallPanel.Visibility =
                _viewModel.IsEverythingNotInstalled && !_viewModel.IsInstallingEverything
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        // 安装进度面板
        if (_step0InstallProgressPanel != null)
        {
            _step0InstallProgressPanel.Visibility = _viewModel.IsInstallingEverything
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // 更新安装进度
        if (_step0InstallProgressBar != null)
        {
            _step0InstallProgressBar.Value = _viewModel.InstallProgressPercentage;
        }
        if (_step0InstallStatusText != null)
        {
            _step0InstallStatusText.Text = _viewModel.InstallStatusText;
        }

        RefreshNavigationButtons();
    }

    /// <summary>构建检测结果摘要面板</summary>
    private StackPanel BuildDetectionResultSummary()
    {
        var panel = new StackPanel();

        var resultBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 8, 0, 0)
        };

        var resultStack = new StackPanel();

        // 根据实际检测结果更新（SetResultSummary 在 UpdateStep0UI 中调用）
        resultStack.Children.Add(new TextBlock
        {
            Text = "检测结果将在检测完成后显示。",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99))
        });

        resultBorder.Child = resultStack;
        panel.Children.Add(resultBorder);

        return panel;
    }

    /// <summary>构建索引等待选项面板</summary>
    private StackPanel BuildIndexWaitOptionsPanel()
    {
        var panel = new StackPanel();

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xE1)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xF9, 0xA8, 0x25)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 12, 0, 0)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "⚠ Everything 正在构建索引",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x7F, 0x17)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        stack.Children.Add(new TextBlock
        {
            Text = "当前索引未完成，扫描结果可能不完整。建议等待索引完成后使用。",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var waitBtn = new Button
        {
            Content = "等待完成",
            Width = 110,
            Height = 34,
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 10, 0)
        };
        waitBtn.Click += (s, e) =>
        {
            _viewModel.StartIndexWaitCommand.Execute(1);
            UpdateStep0UI();
        };

        var bgWaitBtn = new Button
        {
            Content = "后台等待",
            Width = 100,
            Height = 34,
            FontSize = 13,
            Margin = new Thickness(0, 0, 10, 0)
        };
        bgWaitBtn.Click += (s, e) =>
        {
            _viewModel.StartBackgroundWaitCommand.Execute(null);
            UpdateStep0UI();
        };

        var skipBtn = new Button
        {
            Content = "跳过",
            Width = 80,
            Height = 34,
            FontSize = 13
        };
        skipBtn.Click += (s, e) =>
        {
            _viewModel.ExecuteSkipIndexWait();
            UpdateStep0UI();
        };

        btnPanel.Children.Add(waitBtn);
        btnPanel.Children.Add(bgWaitBtn);
        btnPanel.Children.Add(skipBtn);

        stack.Children.Add(btnPanel);
        border.Child = stack;
        panel.Children.Add(border);

        return panel;
    }

    /// <summary>构建索引等待中面板（前台轮询进度）</summary>
    private StackPanel BuildIndexWaitingPanel()
    {
        var panel = new StackPanel();

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFD)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 12, 0, 0)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "⏳ 正在等待 Everything 索引构建完成…",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        _step0WaitElapsedText = new TextBlock
        {
            Text = "已等待 0 分 0 秒",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 10)
        };
        stack.Children.Add(_step0WaitElapsedText);

        stack.Children.Add(new TextBlock
        {
            Text = "最长等待 120 秒，超时后将自动进入警告模式。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Width = 400,
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        };
        stack.Children.Add(progressBar);

        var cancelBtn = new Button
        {
            Content = "取消等待",
            Width = 110,
            Height = 32,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        cancelBtn.Click += (s, e) =>
        {
            _viewModel.CancelIndexWaitCommand.Execute(null);
        };
        stack.Children.Add(cancelBtn);

        border.Child = stack;
        panel.Children.Add(border);

        return panel;
    }

    /// <summary>构建错误信息面板</summary>
    private StackPanel CreateErrorPanel(string title, string message)
    {
        var panel = new StackPanel();

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE9)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 8, 0, 0)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"✕ {title}",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var errorText = new TextBlock
        {
            Name = "ErrorTextBlock",
            Text = message,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(errorText);

        border.Child = stack;
        panel.Children.Add(border);

        return panel;
    }

    /// <summary>构建 Everything 安装选项面板（Everything 未安装时显示）</summary>
    private StackPanel BuildEverythingInstallPanel()
    {
        var panel = new StackPanel();

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 12, 0, 0)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "Everything 搜索引擎未安装",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Everything 是本工具的高性能文件索引引擎，必须安装才能使用。点击下方按钮一键安装 Everything 便携版到本工具目录下。",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var installBtn = new Button
        {
            Content = "一键安装 Everything",
            Width = 160,
            Height = 34,
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 10, 0)
        };
        installBtn.Click += (s, e) =>
        {
            _viewModel.InstallEverythingCommand.Execute(null);
            UpdateStep0UI();
        };

        var exitBtn = new Button
        {
            Content = "退出",
            Width = 80,
            Height = 34,
            FontSize = 13
        };
        exitBtn.Click += (s, e) => Application.Current.Shutdown();

        btnPanel.Children.Add(installBtn);
        btnPanel.Children.Add(exitBtn);
        stack.Children.Add(btnPanel);
        border.Child = stack;
        panel.Children.Add(border);

        return panel;
    }

    /// <summary>构建 Everything 安装进度面板</summary>
    private StackPanel BuildEverythingInstallProgressPanel()
    {
        var panel = new StackPanel();

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFD)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 12, 0, 0)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "正在安装 Everything…",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
            Margin = new Thickness(0, 0, 0, 12)
        });

        _step0InstallStatusText = new TextBlock
        {
            Text = "准备中…",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(_step0InstallStatusText);

        _step0InstallProgressBar = new ProgressBar
        {
            Width = 400,
            Height = 8,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        };
        stack.Children.Add(_step0InstallProgressBar);

        var cancelBtn = new Button
        {
            Content = "取消",
            Width = 100,
            Height = 32,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        cancelBtn.Click += (s, e) =>
        {
            _viewModel.CancelInstallCommand.Execute(null);
        };
        stack.Children.Add(cancelBtn);

        border.Child = stack;
        panel.Children.Add(border);

        return panel;
    }

    // ================================================================
    // Step1: 欢迎与模式选择
    // ================================================================

    private UIElement BuildStep1_Welcome()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "欢迎使用智能磁盘清理工具",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Margin = new Thickness(0, 0, 0, 10)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "本工具帮助您安全、智能地清理磁盘空间。在开始之前，请选择适合您的使用模式。",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 28)
        });

        // 简单模式卡片
        var simpleCard = CreateModeCard(
            "🌿 简单模式（推荐）",
            "• 语义标签引导，一目了然\n• 一键安全清理，无需专业知识\n• 隐藏技术细节，专注核心功能",
            true,
            () =>
            {
                _viewModel.SelectedUserMode = UserMode.Simple;
            });
        panel.Children.Add(simpleCard);

        // 专家模式卡片
        var expertCard = CreateModeCard(
            "🔧 专家模式",
            "• 展示详细规则匹配与决策理由\n• 可自定义清理规则与阈值\n• 支持 AI 智能顾问辅助决策",
            false,
            () =>
            {
                _viewModel.SelectedUserMode = UserMode.Expert;
            });
        panel.Children.Add(expertCard);

        return panel;
    }

    /// <summary>创建模式选择卡片（Step1 使用）</summary>
    private Border CreateModeCard(string title, string description, bool isSimple, Action onSelect)
    {
        var border = new Border
        {
            Background = isSimple
                ? new SolidColorBrush(Color.FromRgb(0xF5, 0xF9, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xE1)),
            BorderBrush = isSimple
                ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))
                : new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 0, 14),
            Cursor = Cursors.Hand
        };

        var stack = new StackPanel();
        var radioBtn = new RadioButton
        {
            Content = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            GroupName = "UserMode",
            IsChecked = isSimple ? _viewModel.IsSimpleMode : _viewModel.IsExpertMode,
            Margin = new Thickness(0, 0, 0, 8)
        };
        radioBtn.Checked += (s, e) => onSelect();
        stack.Children.Add(radioBtn);

        foreach (var line in description.Split('\n'))
        {
            stack.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                Margin = new Thickness(20, 0, 0, 2)
            });
        }

        border.MouseLeftButtonDown += (s, e) =>
        {
            radioBtn.IsChecked = true;
            onSelect();
        };

        border.Child = stack;
        return border;
    }

    // ================================================================
    // Step2: 用户画像（仅专家模式）
    // ================================================================

    private UIElement BuildStep2_Profile()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "用户画像（可选）",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "帮助我们了解您的使用场景，以优化清理策略和推荐规则。",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        });

        // 职业选择
        panel.Children.Add(new TextBlock
        {
            Text = "您的职业",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var professionCombo = new ComboBox
        {
            Width = 280,
            Height = 34,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 18)
        };
        professionCombo.Items.Add("软件工程师");
        professionCombo.Items.Add("运维/系统管理员");
        professionCombo.Items.Add("设计师");
        professionCombo.Items.Add("学生");
        professionCombo.Items.Add("普通用户");
        professionCombo.Items.Add("其他");
        professionCombo.SelectionChanged += (s, e) =>
        {
            if (professionCombo.SelectedItem is ComboBoxItem item)
                _viewModel.UserProfession = item.Content?.ToString() ?? "";
            else if (professionCombo.SelectedItem is string str)
                _viewModel.UserProfession = str;
        };
        // 恢复已保存的值
        if (!string.IsNullOrEmpty(_viewModel.UserProfession))
        {
            professionCombo.Text = _viewModel.UserProfession;
        }
        panel.Children.Add(professionCombo);

        // 设备用途
        panel.Children.Add(new TextBlock
        {
            Text = "设备主要用途",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var usageCombo = new ComboBox
        {
            Width = 280,
            Height = 34,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 18)
        };
        usageCombo.Items.Add("软件开发");
        usageCombo.Items.Add("办公文档");
        usageCombo.Items.Add("娱乐/游戏");
        usageCombo.Items.Add("图形设计/视频编辑");
        usageCombo.Items.Add("服务器/数据库");
        usageCombo.Items.Add("日常通用");
        usageCombo.SelectionChanged += (s, e) =>
        {
            if (usageCombo.SelectedItem is ComboBoxItem item)
                _viewModel.DeviceUsage = item.Content?.ToString() ?? "";
            else if (usageCombo.SelectedItem is string str)
                _viewModel.DeviceUsage = str;
        };
        if (!string.IsNullOrEmpty(_viewModel.DeviceUsage))
        {
            usageCombo.Text = _viewModel.DeviceUsage;
        }
        panel.Children.Add(usageCombo);

        panel.Children.Add(new TextBlock
        {
            Text = "以上信息仅用于优化推荐策略，不会上传或分享。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(0, 10, 0, 0)
        });

        return panel;
    }

    // ================================================================
    // Step3: 保护与排除配置
    // ================================================================

    private UIElement BuildStep3_Exclusions()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "保护与排除设置",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "保护重要目录和文件类型，防止误删。以下目录已被自动保护。",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        });

        // 自动保护目录
        panel.Children.Add(new TextBlock
        {
            Text = "🛡️ 自动保护目录（不可移除）",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var protectedBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 16)
        };
        var protectedStack = new StackPanel();
        protectedStack.Children.Add(new TextBlock
        {
            Text = "• C:\\Windows — 系统目录（硬保护）",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        protectedStack.Children.Add(new TextBlock
        {
            Text = "• C:\\Program Files — 应用程序目录（硬保护）",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        protectedStack.Children.Add(new TextBlock
        {
            Text = "• Docker / WSL 数据目录（自动检测）",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))
        });
        protectedBorder.Child = protectedStack;
        panel.Children.Add(protectedBorder);

        // 手动排除目录
        panel.Children.Add(new TextBlock
        {
            Text = "📁 手动添加排除目录（可选）",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var excludeGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        excludeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        excludeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var excludeInput = new TextBox
        {
            Height = 32,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0)
        };
        excludeGrid.Children.Add(excludeInput);
        Grid.SetColumn(excludeInput, 0);

        var addBtn = new Button
        {
            Content = "添加",
            Width = 64,
            Height = 32,
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0)
        };
        addBtn.Click += (s, e) =>
        {
            var path = excludeInput.Text.Trim();
            if (!string.IsNullOrEmpty(path) && !_viewModel.UserConfig.UserExcludedDirs.Contains(path))
            {
                _viewModel.UserConfig.UserExcludedDirs.Add(path);
                excludeInput.Text = "";
                _logger.LogInformation("添加排除目录: {Path}", path);
            }
        };
        excludeGrid.Children.Add(addBtn);
        Grid.SetColumn(addBtn, 1);
        panel.Children.Add(excludeGrid);

        panel.Children.Add(new TextBlock
        {
            Text = "支持通配符，如 C:\\Users\\*\\Downloads\\*",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(0, 0, 0, 16)
        });

        // 排除文件扩展名
        panel.Children.Add(new TextBlock
        {
            Text = "📎 排除文件扩展名（可选）",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var extInput = new TextBox
        {
            Height = 32,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 0, 4)
        };
        panel.Children.Add(extInput);

        panel.Children.Add(new TextBlock
        {
            Text = "多个扩展名用逗号分隔，如 .pst, .ost, .vhd",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99))
        });

        return panel;
    }

    // ================================================================
    // Step4: AI 配置
    // ================================================================

    private UIElement BuildStep4_AIConfig()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "AI 智能顾问配置（可选）",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "AI 顾问可为您提供智能清理建议，帮助识别不确定的文件类型。不启用 AI 不会影响基本清理功能。",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        });

        // 启用开关
        var enableCheck = new CheckBox
        {
            IsChecked = _viewModel.AIEnabled,
            Content = "启用 AI 智能顾问",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 16)
        };
        enableCheck.Checked += (s, e) => _viewModel.AIEnabled = true;
        enableCheck.Unchecked += (s, e) => _viewModel.AIEnabled = false;
        panel.Children.Add(enableCheck);

        // 表单字段
        var fields = new (string label, string bindValue, Action<string> setter)[]
        {
            ("API 地址", _viewModel.AIApiUrl, v => _viewModel.AIApiUrl = v),
            ("API Key（加密存储）", _viewModel.AIApiKey, v => _viewModel.AIApiKey = v),
            ("模型名称", _viewModel.AIModel, v => _viewModel.AIModel = v),
        };

        foreach (var (label, bindValue, setter) in fields)
        {
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Margin = new Thickness(0, 0, 0, 6)
            });

            var textBox = new TextBox
            {
                Text = bindValue,
                Width = 380,
                Height = 32,
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 14)
            };
            textBox.TextChanged += (s, e) => setter(textBox.Text);
            panel.Children.Add(textBox);
        }

        // 每日限额
        panel.Children.Add(new TextBlock
        {
            Text = "每日调用限额",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var limitBox = new TextBox
        {
            Text = _viewModel.AIDailyLimit.ToString(),
            Width = 120,
            Height = 32,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 14)
        };
        limitBox.TextChanged += (s, e) =>
        {
            if (int.TryParse(limitBox.Text, out int val))
                _viewModel.AIDailyLimit = val;
        };
        panel.Children.Add(limitBox);

        // AI 服务类型
        panel.Children.Add(new TextBlock
        {
            Text = "AI 服务类型",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var serviceCombo = new ComboBox
        {
            Width = 200,
            Height = 32,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 14)
        };
        serviceCombo.Items.Add("OpenAI API");
        serviceCombo.Items.Add("本地 Ollama");
        serviceCombo.Items.Add("自定义 API");
        serviceCombo.SelectionChanged += (s, e) =>
        {
            if (serviceCombo.SelectedItem is ComboBoxItem item)
                _viewModel.AIServiceType = item.Content?.ToString() ?? "custom";
            else if (serviceCombo.SelectedItem is string str)
                _viewModel.AIServiceType = str;
        };
        if (!string.IsNullOrEmpty(_viewModel.AIServiceType))
        {
            // 尝试匹配已保存的服务类型
            for (int i = 0; i < serviceCombo.Items.Count; i++)
            {
                if (serviceCombo.Items[i] is ComboBoxItem cbi &&
                    cbi.Content?.ToString()?.Contains(_viewModel.AIServiceType, StringComparison.OrdinalIgnoreCase) == true)
                {
                    serviceCombo.SelectedIndex = i;
                    break;
                }
            }
        }
        panel.Children.Add(serviceCombo);

        // 测试连接按钮
        var testBtn = new Button
        {
            Content = "测试 AI 连接",
            Width = 130,
            Height = 32,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 8)
        };
        testBtn.Click += async (s, e) =>
        {
            if (!_viewModel.AIEnabled || string.IsNullOrWhiteSpace(_viewModel.AIApiUrl))
            {
                if (_aiTestResultText != null)
                {
                    _aiTestResultText.Text = "⚠ 请先启用 AI 并填写 API 地址";
                    _aiTestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x7F, 0x17));
                }
                return;
            }

            testBtn.IsEnabled = false;
            if (_aiTestResultText != null)
            {
                _aiTestResultText.Text = "⏳ 正在测试连接…";
                _aiTestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
            }

            try
            {
                // 调用真实 AI 服务验证连接（全部使用传入参数，不依赖当前配置）
                var success = await _aiAdvisorService.TestConnectionAsync(
                    _viewModel.AIServiceType ?? "custom",
                    _viewModel.AIApiUrl,
                    _viewModel.AIApiKey ?? string.Empty,
                    _viewModel.AIModel ?? string.Empty);

                if (_aiTestResultText != null)
                {
                    if (success)
                    {
                        _aiTestResultText.Text = "✓ 连接测试通过！AI 服务响应正常。";
                        _aiTestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
                    }
                    else
                    {
                        _aiTestResultText.Text = "✕ 连接失败: 服务返回异常响应，请检查 API 地址和密钥。";
                        _aiTestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
                    }
                }
                _viewModel.IsAIConnectionOk = success;
            }
            catch (Exception ex)
            {
                if (_aiTestResultText != null)
                {
                    _aiTestResultText.Text = $"✕ 连接失败: {ex.Message}";
                    _aiTestResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
                }
                _viewModel.IsAIConnectionOk = false;
                _logger.LogError(ex, "AI 连接测试失败");
            }
            finally
            {
                testBtn.IsEnabled = true;
            }
        };
        panel.Children.Add(testBtn);

        _aiTestResultText = new TextBlock
        {
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 0)
        };
        panel.Children.Add(_aiTestResultText);

        return panel;
    }

    // ================================================================
    // 导航按钮事件处理器
    // ================================================================

    private void NextStep_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_viewModel.IsLastStep)
            {
                // 最后一步 → ViewModel 处理完成逻辑
                _isCompleting = true;
            }
            _viewModel.NextCommand.Execute(null);
            NavigateToStep(_viewModel.CurrentStepIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下一步操作失败");
        }
    }

    private void PreviousStep_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.PreviousCommand.Execute(null);
            NavigateToStep(_viewModel.CurrentStepIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上一步操作失败");
        }
    }

    private void SkipCurrentStep_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.SkipStepCommand.Execute(null);
            NavigateToStep(_viewModel.CurrentStepIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "跳过步骤操作失败");
        }
    }

    private void SkipWizard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = MessageBox.Show(
                "确定要跳过配置向导吗？\n\n将使用默认设置直接进入主界面。您可以稍后在设置中修改所有选项。",
                "跳过配置向导",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _viewModel.SkipWizardCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "跳过向导操作失败");
        }
    }

    // Step0 专用事件（XAML 中引用的 Click 事件）
    private void RetryDetection_Click(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.RunEverythingDetectionAsync().ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStep0UI();
                RefreshNavigationButtons();
            });
        });
    }

    private void SkipIndex_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ExecuteSkipIndexWait();
        UpdateStep0UI();
    }

    private void SimpleMode_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedUserMode = UserMode.Simple;
    }

    private void ExpertMode_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedUserMode = UserMode.Expert;
    }

    private void TestAIConnection_Click(object sender, RoutedEventArgs e)
    {
        // AI 连接测试已在 BuildStep4_AIConfig 中内联处理
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    // ================================================================
    // ViewModel 事件回调
    // ================================================================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 在 UI 线程更新
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(SetupWizardViewModel.IsDetecting):
                case nameof(SetupWizardViewModel.DetectionError):
                case nameof(SetupWizardViewModel.IsDetectionComplete):
                case nameof(SetupWizardViewModel.IsWaitingForIndex):
                case nameof(SetupWizardViewModel.WaitElapsedText):
                case nameof(SetupWizardViewModel.IsIndexTimeout):
                case nameof(SetupWizardViewModel.IsEverythingNotInstalled):
                case nameof(SetupWizardViewModel.IsInstallingEverything):
                case nameof(SetupWizardViewModel.InstallProgressPercentage):
                case nameof(SetupWizardViewModel.InstallStatusText):
                    UpdateStep0UI();
                    break;

                case nameof(SetupWizardViewModel.CanGoNext):
                case nameof(SetupWizardViewModel.CanGoPrevious):
                case nameof(SetupWizardViewModel.CanSkipCurrentStep):
                case nameof(SetupWizardViewModel.IsLastStep):
                    RefreshNavigationButtons();
                    break;

                case nameof(SetupWizardViewModel.CurrentStepIndex):
                    if (!_isNavigating)
                        NavigateToStep(_viewModel.CurrentStepIndex);
                    break;
            }
        });
    }

    private void OnWizardCompleted(object? sender, EventArgs e)
    {
        _isCompleting = true;
        Dispatcher.Invoke(() =>
        {
            _logger.LogInformation("向导完成，窗口即将关闭");
            Close();
        });
    }

    private void OnWizardSkipped(object? sender, EventArgs e)
    {
        _logger.LogInformation("向导被用户跳过");
        // 不需要额外处理，App 层监听此事件后启动主窗口
    }

    // ================================================================
    // 导航按钮刷新
    // ================================================================

    /// <summary>根据 ViewModel 状态刷新导航按钮的可见性和内容</summary>
    private void RefreshNavigationButtons()
    {
        // 上一步按钮 — Step0 时隐藏
        PreviousButton.Visibility = _viewModel.CanGoPrevious
            ? Visibility.Visible : Visibility.Collapsed;

        // 下一步/完成按钮
        NextButton.Content = _viewModel.NextButtonText;
        NextButton.Visibility = (_viewModel.CanGoNext || _viewModel.IsLastStep)
            && !_viewModel.IsDetecting && !string.IsNullOrEmpty(_viewModel.DetectionError) == false
            ? Visibility.Visible : Visibility.Collapsed;

        // 跳过此步骤按钮
        SkipStepButton.Visibility = _viewModel.CanSkipCurrentStep
            ? Visibility.Visible : Visibility.Collapsed;

        // 跳过向导按钮 — 仅在 Step1 时显示
        SkipWizardButton.Visibility =
            _viewModel.CurrentStepIndex == 1 && _viewModel.CanGoNext
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 后台等待模式下恢复窗口显示（由 App 层托盘图标双击调用）。
    /// </summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _logger.LogInformation("从托盘恢复向导窗口");
    }
}
