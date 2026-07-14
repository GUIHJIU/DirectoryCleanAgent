namespace DirectoryCleanAgent.Services;

/// <summary>
/// 目录选择器——封装文件夹浏览对话框，供扫描模式使用。
/// </summary>
public interface IDirectoryPickerService
{
    /// <summary>
    /// 弹出目录选择对话框，返回用户选中的目录绝对路径。
    /// 用户取消时返回 null。
    /// </summary>
    /// <param name="description">对话框标题栏提示文本（应已本地化）</param>
    /// <returns>用户选中的目录绝对路径（普通格式），取消返回 null</returns>
    string? PickDirectory(string description);
}
