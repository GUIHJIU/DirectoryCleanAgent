namespace DirectoryCleanAgent.Models;

/// <summary>
/// ComboBox 数据项模型，实现 Display（显示文本）与 Value（实际值）分离。
/// 支持枚举值与本地化文本的映射，语言切换时重建列表即可刷新显示。
/// </summary>
public class ComboItem
{
    /// <summary>UI 展示文本（可本地化），如 "自动扫描" / "Auto Scan"</summary>
    public string Display { get; init; } = string.Empty;

    /// <summary>实际存储值（枚举、string 等），绑定 SelectedValue</summary>
    public object? Value { get; init; }

    /// <summary>ComboBox 默认显示 Display 内容</summary>
    public override string ToString() => Display;
}
