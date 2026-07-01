namespace DirectoryCleanAgent.Models;

/// <summary>
/// 首次启动向导步骤描述模型。
/// 每个步骤包含标题、描述和可跳过标记，供 SetupWizardWindow 的步骤指示器绑定。
/// </summary>
public class WizardStepInfo
{
    /// <summary>步骤序号（从 0 开始）</summary>
    public int Index { get; init; }

    /// <summary>步骤标题（显示在步骤指示器中）</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>步骤简要描述（显示在内容区顶部）</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>该步骤是否允许用户跳过（使用默认配置）</summary>
    public bool IsSkippable { get; init; }

    /// <summary>该步骤是否已完成（用于步骤指示器状态显示）</summary>
    public bool IsCompleted { get; set; }

    /// <summary>是否当前活动的步骤（用于步骤指示器高亮）</summary>
    public bool IsActive { get; set; }
}
