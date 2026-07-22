using System.Windows;
using System.Windows.Media;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Models;

namespace DirectoryCleanAgent.Views;

/// <summary>
/// 文件详情弹窗 — 展示单个文件的完整决策信息。
/// 接收 FileListItem 作为数据源，非模态显示（ShowDialog）。
/// </summary>
public partial class FileDetailWindow : Window
{
    public FileDetailWindow(FileListItem item, string? ruleVerdictText = null)
    {
        InitializeComponent();
        DataContext = new FileDetailData(item, ruleVerdictText);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

/// <summary>
/// 文件详情窗口的纯数据模型（不可变，仅用于展示）。
/// </summary>
internal sealed class FileDetailData
{
    public string FilePath { get; }
    public string FullPath { get; }
    public string SizeText { get; }
    public string LastWriteTimeText { get; }
    public string Extension { get; }
    public string SemanticCategory { get; }
    public string SemanticCategoryIcon { get; }
    public string FinalActionText { get; }
    public Brush FinalActionBackground { get; }
    public string AiStatusIcon { get; }
    public string AiStatusText { get; }
    public string AiConfidenceText { get; }
    public string AiExplanationText { get; }
    public string RuleVerdictText { get; }

    public FileDetailData(FileListItem item, string? ruleVerdictText)
    {
        FilePath = item.FilePath;
        FullPath = item.FullPath;
        SizeText = item.SizeText;
        LastWriteTimeText = item.LastWriteTimeText;
        Extension = string.IsNullOrEmpty(item.Extension) ? "(无)" : item.Extension;
        SemanticCategory = item.SemanticCategory;
        SemanticCategoryIcon = item.SemanticCategoryIcon;
        FinalActionText = item.FinalActionText;
        FinalActionBackground = GetFinalActionBrush(item.FinalAction);
        AiStatusIcon = item.AiStatusIcon;
        AiStatusText = item.AiStatusText;
        AiConfidenceText = item.AiConfidence.HasValue
            ? $"{item.AiConfidence.Value:P1}"
            : "—";
        AiExplanationText = string.IsNullOrEmpty(item.AiExplanation)
            ? "尚未进行 AI 分析。点击文件列表 AI 列的\"❓ 点击分析\"或右键菜单的\"🤖 AI 分析此文件\"可触发分析。"
            : item.AiExplanation;
        RuleVerdictText = ruleVerdictText ?? GetDefaultVerdictText(item);
    }

    private static string GetDefaultVerdictText(FileListItem item)
    {
        // 基于 FinalAction 推断规则裁决文本
        return item.FinalAction switch
        {
            FinalAction.AutoDelete => "匹配自动删除规则（系统临时文件等）",
            FinalAction.SuggestDelete => "匹配启发式建议删除规则",
            FinalAction.ManualReview => "未命中规则或需人工确认",
            FinalAction.Protected => "受保护（硬规则禁止或用户排除）",
            _ => "未知"
        };
    }

    private static Brush GetFinalActionBrush(FinalAction action)
    {
        return action switch
        {
            FinalAction.AutoDelete => new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9)),   // 绿色
            FinalAction.SuggestDelete => new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFD)), // 蓝色
            FinalAction.ManualReview => new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE0)),  // 橙色
            FinalAction.Protected => new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0xEE)),     // 红色
            _ => new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5))
        };
    }
}
