namespace DirectoryCleanAgent.Core.Enums;

/// <summary>
/// 最终操作决策（由决策引擎仲裁输出）
/// </summary>
public enum FinalAction
{
    /// <summary>自动安全删除：无需人工确认，可直接清理</summary>
    AutoDelete = 0,

    /// <summary>建议删除：推荐清理，需用户勾选确认</summary>
    SuggestDelete = 1,

    /// <summary>需人工确认：存在风险，必须用户逐项审核</summary>
    ManualReview = 2,

    /// <summary>受保护：禁止删除（系统关键文件、用户白名单等）</summary>
    Protected = 3
}
