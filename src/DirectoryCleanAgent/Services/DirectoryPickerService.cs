using System.Windows.Forms;

namespace DirectoryCleanAgent.Services;

/// <summary>
/// 目录选择器实现——使用 WinForms FolderBrowserDialog 弹出目录选择对话框。
/// </summary>
public sealed class DirectoryPickerService : IDirectoryPickerService
{
    /// <inheritdoc />
    public string? PickDirectory(string description)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        return dialog.ShowDialog() == DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }
}
