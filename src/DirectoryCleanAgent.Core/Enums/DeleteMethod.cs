namespace DirectoryCleanAgent.Core.Enums;

/// <summary>
/// 文件删除方式
/// </summary>
public enum DeleteMethod
{
    /// <summary>放入回收站（可恢复）</summary>
    RecycleBin = 0,

    /// <summary>永久删除（不可恢复，需二次确认）</summary>
    Permanent = 1
}
