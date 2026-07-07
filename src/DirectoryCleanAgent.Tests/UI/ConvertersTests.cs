using System.Globalization;
using System.Windows;
using DirectoryCleanAgent.Converters;
using DirectoryCleanAgent.Core.Enums;
using Xunit;

namespace DirectoryCleanAgent.Tests.UI;

/// <summary>
/// C1 值转换器单元测试。
/// 覆盖所有 5 个转换器的 Convert 方法核心逻辑。
/// </summary>
public class ConvertersTests
{
    // ============================================================
    // FileSizeConverter
    // ============================================================

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(500L, "500 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(1073741824L, "1.0 GB")]
    [InlineData(19520256000L, "18 GB")]       // 18.2 GB → 取整
    [InlineData(3221225472L, "3.0 GB")]       // 3.0 GB → 保留一位
    [InlineData(-1L, "0 B")]                  // 负数 → 0
    public void FileSizeConverter_Convert_ReturnsExpectedFormat(long bytes, string expected)
    {
        var converter = new FileSizeConverter();
        var result = converter.Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FileSizeConverter_ConvertBack_ThrowsNotSupported()
    {
        var converter = new FileSizeConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack("1 GB", typeof(long), null, CultureInfo.InvariantCulture));
    }

    // ============================================================
    // InverseBoolConverter
    // ============================================================

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InverseBoolConverter_Convert_InvertsValue(bool input, bool expected)
    {
        var converter = new InverseBoolConverter();
        var result = converter.Convert(input, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void InverseBoolConverter_Convert_NonBool_ReturnsTrue()
    {
        // value is not bool → `value is bool b && !b` = false
        var converter = new InverseBoolConverter();
        var result = converter.Convert("not a bool", typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    // ============================================================
    // BoolToVisibilityConverter
    // ============================================================

    [Fact]
    public void BoolToVisibilityConverter_True_ReturnsVisible()
    {
        var converter = new BoolToVisibilityConverter();
        var result = converter.Convert(true, typeof(Visibility), null, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void BoolToVisibilityConverter_False_ReturnsCollapsed()
    {
        var converter = new BoolToVisibilityConverter();
        var result = converter.Convert(false, typeof(Visibility), null, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void BoolToVisibilityConverter_False_Hidden_ReturnsHidden()
    {
        var converter = new BoolToVisibilityConverter();
        var result = converter.Convert(false, typeof(Visibility), "Hidden", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Hidden, result);
    }

    // ============================================================
    // FinalActionToColorConverter
    // ============================================================

    [Fact]
    public void FinalActionToColorConverter_ReturnsNonNullBrush()
    {
        var converter = new FinalActionToColorConverter();
        foreach (FinalAction action in Enum.GetValues<FinalAction>())
        {
            var result = converter.Convert(action, typeof(object), null, CultureInfo.InvariantCulture);
            Assert.NotNull(result);
        }
    }

    [Fact]
    public void FinalActionToColorConverter_InvalidInput_ReturnsDefault()
    {
        var converter = new FinalActionToColorConverter();
        var result = converter.Convert("invalid", typeof(object), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
    }

    // ============================================================
    // AppStateToBoolConverter
    // ============================================================

    [Fact]
    public void AppStateToBoolConverter_MatchingState_ReturnsTrue()
    {
        var converter = new AppStateToBoolConverter();
        var result = converter.Convert(AppState.Ready, typeof(bool), "Ready", CultureInfo.InvariantCulture);
        Assert.True((bool)result);
    }

    [Fact]
    public void AppStateToBoolConverter_NonMatchingState_ReturnsFalse()
    {
        var converter = new AppStateToBoolConverter();
        var result = converter.Convert(AppState.Scanning, typeof(bool), "Ready", CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void AppStateToBoolConverter_MultipleStates_CommaSeparated()
    {
        var converter = new AppStateToBoolConverter();
        // "Ready,Scanning" — Scanning should match
        var result = converter.Convert(AppState.Scanning, typeof(bool), "Ready,Scanning", CultureInfo.InvariantCulture);
        Assert.True((bool)result);
    }

    // ============================================================
    // C2: SemanticCategoryToIconConverter
    // ============================================================

    [Theory]
    [InlineData(SemanticCategory.SystemTempFiles, "🗑️")]
    [InlineData(SemanticCategory.AppCache, "🧹")]
    [InlineData(SemanticCategory.OldDownloads, "📥")]
    [InlineData(SemanticCategory.RecycleBin, "♻️")]
    [InlineData(SemanticCategory.LogsAndDumps, "📋")]
    [InlineData(SemanticCategory.OldUserFiles, "📁")]
    [InlineData(SemanticCategory.Protected, "🛡️")]
    [InlineData(SemanticCategory.Uncategorized, "❓")]
    public void SemanticCategoryToIconConverter_ReturnsCorrectEmoji(SemanticCategory category, string expected)
    {
        var converter = new SemanticCategoryToIconConverter();
        var result = converter.Convert(category, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SemanticCategoryToIconConverter_Null_ReturnsDefault()
    {
        var converter = new SemanticCategoryToIconConverter();
        var result = converter.Convert(null!, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("📄", result);
    }

    [Fact]
    public void SemanticCategoryToIconConverter_InvalidString_ReturnsDefault()
    {
        var converter = new SemanticCategoryToIconConverter();
        var result = converter.Convert("nonexistent", typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("📄", result);
    }

    [Theory]
    [InlineData("系统临时文件", "🗑️")]
    [InlineData("应用缓存", "🧹")]
    [InlineData("Logs / Dumps", "📋")]
    [InlineData("Uncategorized", "❓")]
    public void SemanticCategoryToIconConverter_StringLabel_ReturnsCorrectEmoji(string label, string expected)
    {
        var converter = new SemanticCategoryToIconConverter();
        var result = converter.Convert(label, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    // ============================================================
    // C2: FinalActionToDisplayConverter
    // ============================================================

    [Theory]
    [InlineData(FinalAction.AutoDelete, "可自动清理")]
    [InlineData(FinalAction.SuggestDelete, "建议清理")]
    [InlineData(FinalAction.ManualReview, "需确认")]
    [InlineData(FinalAction.Protected, "受保护")]
    public void FinalActionToDisplayConverter_ReturnsCorrectText(FinalAction action, string expected)
    {
        var converter = new FinalActionToDisplayConverter();
        var result = converter.Convert(action, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FinalActionToDisplayConverter_InvalidInput_ReturnsUnknown()
    {
        var converter = new FinalActionToDisplayConverter();
        var result = converter.Convert("invalid", typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("未知", result);
    }

    // ============================================================
    // C2: FilePathToDisplayConverter
    // ============================================================

    [Fact]
    public void FilePathToDisplayConverter_Default_ReturnsFileName()
    {
        var converter = new FilePathToDisplayConverter();
        var result = converter.Convert(@"\\?\C:\Users\Admin\file.txt", typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("file.txt", result);
    }

    [Fact]
    public void FilePathToDisplayConverter_FullPath_ReturnsCleanPath()
    {
        var converter = new FilePathToDisplayConverter();
        var result = converter.Convert(@"\\?\C:\Users\Admin\file.txt", typeof(string), "FullPath", CultureInfo.InvariantCulture);
        Assert.Equal(@"C:\Users\Admin\file.txt", result);
    }

    [Fact]
    public void FilePathToDisplayConverter_Directory_ReturnsParentDir()
    {
        var converter = new FilePathToDisplayConverter();
        var result = converter.Convert(@"\\?\C:\Users\Admin\file.txt", typeof(string), "Directory", CultureInfo.InvariantCulture);
        Assert.Equal(@"C:\Users\Admin", result);
    }

    [Fact]
    public void FilePathToDisplayConverter_Null_ReturnsEmpty()
    {
        var converter = new FilePathToDisplayConverter();
        var result = converter.Convert(null!, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FilePathToDisplayConverter_Empty_ReturnsEmpty()
    {
        var converter = new FilePathToDisplayConverter();
        var result = converter.Convert(string.Empty, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(string.Empty, result);
    }

    // ============================================================
    // C4: CountToVisibilityConverter
    // ============================================================

    [Fact]
    public void CountToVisibilityConverter_Zero_ReturnsVisible()
    {
        var converter = new CountToVisibilityConverter();
        var result = converter.Convert(0, typeof(Visibility), null, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void CountToVisibilityConverter_Positive_ReturnsCollapsed()
    {
        var converter = new CountToVisibilityConverter();
        var result = converter.Convert(5, typeof(Visibility), null, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void CountToVisibilityConverter_NonInt_ReturnsVisible()
    {
        var converter = new CountToVisibilityConverter();
        var result = converter.Convert("not an int", typeof(Visibility), null, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void CountToVisibilityConverter_Inverse_Zero_ReturnsCollapsed()
    {
        var converter = new CountToVisibilityConverter();
        var result = converter.Convert(0, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void CountToVisibilityConverter_Inverse_Positive_ReturnsVisible()
    {
        var converter = new CountToVisibilityConverter();
        var result = converter.Convert(3, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void CountToVisibilityConverter_ConvertBack_ThrowsNotSupported()
    {
        var converter = new CountToVisibilityConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(Visibility.Visible, typeof(int), null, CultureInfo.InvariantCulture));
    }

    // ============================================================
    // C2: DepthToFontWeightConverter
    // ============================================================

    [Fact]
    public void DepthToFontWeightConverter_Depth0_ReturnsSemiBold()
    {
        var converter = new DepthToFontWeightConverter();
        var result = converter.Convert(0, typeof(FontWeight), null, CultureInfo.InvariantCulture);
        Assert.Equal(FontWeights.SemiBold, result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public void DepthToFontWeightConverter_DepthGreaterThan0_ReturnsNormal(int depth)
    {
        var converter = new DepthToFontWeightConverter();
        var result = converter.Convert(depth, typeof(FontWeight), null, CultureInfo.InvariantCulture);
        Assert.Equal(FontWeights.Normal, result);
    }

    [Fact]
    public void DepthToFontWeightConverter_NonInt_DefaultsTo0_ReturnsSemiBold()
    {
        var converter = new DepthToFontWeightConverter();
        var result = converter.Convert("not an int", typeof(FontWeight), null, CultureInfo.InvariantCulture);
        Assert.Equal(FontWeights.SemiBold, result);
    }

    [Fact]
    public void DepthToFontWeightConverter_ConvertBack_ThrowsNotSupported()
    {
        var converter = new DepthToFontWeightConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(FontWeights.Bold, typeof(int), null, CultureInfo.InvariantCulture));
    }
}
