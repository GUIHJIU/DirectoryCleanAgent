using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.ViewModels.Base;

namespace DirectoryCleanAgent.Models;

/// <summary>
/// 文件列表行模型 — 对应 DataGrid 中的每一行。
/// 支持复选框勾选、排序键透传、AI 状态展示。
/// 数据源来自 FileDecisionCache 或 Everything 全量结果。
/// </summary>
public class FileListItem : ViewModelBase
{
    private bool _isChecked;
    private string _semanticCategory = string.Empty;
    private string _semanticCategoryIcon = "📄";
    private string _aiStatusText = "❓ 点击分析";
    private string _aiStatusIcon = "❓";

    /// <summary>显示用短路径（仅文件名）</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>完整路径（\\?\ 格式，用于 Everything 查询和文件操作）</summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>文件大小（字节）</summary>
    public long SizeBytes { get; set; }

    /// <summary>格式化后的文件大小文本</summary>
    public string SizeText { get; set; } = "0 B";

    /// <summary>最后修改时间（UTC）</summary>
    public DateTime LastWriteTime { get; set; }

    /// <summary>格式化后的修改时间文本</summary>
    public string LastWriteTimeText { get; set; } = string.Empty;

    /// <summary>文件扩展名（小写，含点号，如 ".log"）</summary>
    public string? Extension { get; set; }

    /// <summary>语义分类本地化文本（如"系统临时文件"）</summary>
    public string SemanticCategory
    {
        get => _semanticCategory;
        set => SetProperty(ref _semanticCategory, value);
    }

    /// <summary>语义分类图标（Emoji）</summary>
    public string SemanticCategoryIcon
    {
        get => _semanticCategoryIcon;
        set => SetProperty(ref _semanticCategoryIcon, value);
    }

    /// <summary>最终操作决策枚举</summary>
    public FinalAction FinalAction { get; set; }

    /// <summary>最终操作建议的本地化文本（如"可自动清理"）</summary>
    public string FinalActionText { get; set; } = string.Empty;

    /// <summary>AI 分析标签（safe / unknown / risky）</summary>
    public string? AiLabel { get; set; }

    /// <summary>AI 分析置信度（0.0 ~ 1.0）</summary>
    public double? AiConfidence { get; set; }

    /// <summary>AI 分析说明文本</summary>
    public string? AiExplanation { get; set; }

    /// <summary>AI 状态展示文本</summary>
    public string AiStatusText
    {
        get => _aiStatusText;
        set => SetProperty(ref _aiStatusText, value);
    }

    /// <summary>AI 状态图标</summary>
    public string AiStatusIcon
    {
        get => _aiStatusIcon;
        set => SetProperty(ref _aiStatusIcon, value);
    }

    /// <summary>用户是否可对此文件执行操作（FinalAction != Protected）</summary>
    public bool IsUserActionable => FinalAction != FinalAction.Protected;

    /// <summary>用户复选框勾选状态（用于批量操作选择）</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    /// <summary>文件标识键（文件路径，可用于后续 Everything 精确重查）</summary>
    public string? EverythingSortKey { get; set; }

    /// <summary>原始 FileDecisionCache 中的 FilePath 键（用于缓存回写和增量刷新）</summary>
    public string CacheKey { get; set; } = string.Empty;

    /// <summary>
    /// 根据 AI 分析状态更新展示文本和图标。
    /// 未分析 → "❓ 点击分析", 分析中 → "⏳ 分析中", 完成 → 依据 AiLabel 展示对应图标。
    /// </summary>
    public void UpdateAiDisplay()
    {
        if (string.IsNullOrEmpty(AiLabel))
        {
            AiStatusText = "❓ 点击分析";
            AiStatusIcon = "❓";
        }
        else if (AiLabel == "analyzing")
        {
            AiStatusText = "⏳ 分析中";
            AiStatusIcon = "⏳";
        }
        else
        {
            (AiStatusIcon, AiStatusText) = AiLabel switch
            {
                "safe" => ("✅", "安全"),
                "unknown" => ("❓", "未知"),
                "risky" => ("⚠️", "风险"),
                _ => ("❓", AiLabel)
            };
        }
    }
}
