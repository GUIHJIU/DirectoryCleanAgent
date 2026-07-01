using DirectoryCleanAgent.Core.PathHandling;

namespace DirectoryCleanAgent.Tests.Core.PathHandling;

/// <summary>
/// PathNormalizer 单元测试。
///
/// 覆盖范围：
/// - Normalize: 绝对路径、UNC路径、设备路径、已标准化路径（幂等）、相对路径/null/空/空白异常
/// - Denormalize: 扩展路径还原、UNC扩展还原、非扩展原样返回、null/空异常
/// - IsNormalized: 扩展/非扩展/null/空判断
/// - Validate: 合法路径通过、控制字符/非法字符异常、null/空异常
/// - 边界: 尾部空格、超长路径、往返一致性
/// </summary>
public class PathNormalizerTests
{
    #region Normalize — 核心转换

    [Fact]
    public void Normalize_ValidDosPath_ShouldAddExtendedPrefix()
    {
        // Arrange
        const string input = @"C:\Windows\System32\drivers\etc\hosts";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert
        Assert.Equal(@"\\?\C:\Windows\System32\drivers\etc\hosts", result);
    }

    [Fact]
    public void Normalize_RootDrive_ShouldAddExtendedPrefix()
    {
        // Arrange
        const string input = @"D:\";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert
        Assert.Equal(@"\\?\D:\", result);
    }

    [Fact]
    public void Normalize_UncPath_ShouldConvertToUncExtendedFormat()
    {
        // Arrange
        const string input = @"\\server\share\folder\file.txt";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert
        Assert.Equal(@"\\?\UNC\server\share\folder\file.txt", result);
    }

    [Fact]
    public void Normalize_UncPathRoot_ShouldConvertCorrectly()
    {
        // Arrange
        const string input = @"\\192.168.1.1\public";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert
        Assert.Equal(@"\\?\UNC\192.168.1.1\public", result);
    }

    [Fact]
    public void Normalize_AlreadyNormalized_ShouldReturnSameValue()
    {
        // Arrange
        const string input = @"\\?\C:\Program Files\dotnet\dotnet.exe";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert
        Assert.Same(input, result); // 幂等：应返回同一引用
    }

    [Fact]
    public void Normalize_AlreadyNormalizedUnc_ShouldReturnSameValue()
    {
        // Arrange
        const string input = @"\\?\UNC\server\share\file.bin";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert
        Assert.Same(input, result);
    }

    [Fact]
    public void Normalize_DevicePath_ShouldReplacePrefixWithExtended()
    {
        // Arrange
        const string input = @"\\.\C:\Windows\explorer.exe";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert
        Assert.Equal(@"\\?\C:\Windows\explorer.exe", result);
    }

    [Fact]
    public void Normalize_WithTrailingSpaces_ShouldPreserveThem()
    {
        // Arrange
        const string input = @"C:\Temp\file   "; // 三个尾部空格（用户可能误操作）

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert — \\?\ 模式下尾部空格被保留
        Assert.Equal(@"\\?\C:\Temp\file   ", result);
    }

    [Fact]
    public void Normalize_WithTrailingDots_ShouldPreserveThem()
    {
        // Arrange
        const string input = @"C:\Temp\file...";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert — \\?\ 模式下尾部点号被保留
        Assert.Equal(@"\\?\C:\Temp\file...", result);
    }

    [Fact]
    public void Normalize_NullPath_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() => PathNormalizer.Normalize(null!));
        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void Normalize_EmptyPath_ShouldThrowArgumentException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => PathNormalizer.Normalize(""));
        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void Normalize_WhitespacePath_ShouldThrowArgumentException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => PathNormalizer.Normalize("   "));
        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void Normalize_RelativePath_ShouldThrowArgumentException()
    {
        // Arrange
        const string input = @"folder\subfolder\file.txt";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => PathNormalizer.Normalize(input));
        Assert.Contains("相对路径", ex.Message);
        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void Normalize_RelativePathWithDot_ShouldThrowArgumentException()
    {
        // Arrange
        const string input = @".\config.json";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => PathNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_RelativePathWithDoubleDot_ShouldThrowArgumentException()
    {
        // Arrange
        const string input = @"..\data\file.db";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => PathNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_MixedSeparators_ShouldPreserveAsIs()
    {
        // Arrange — 混合使用 / 和 \ 分隔符
        const string input = @"C:\Windows/System32\drivers/etc";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert — 不做规范化，仅添加前缀
        Assert.Equal(@"\\?\C:\Windows/System32\drivers/etc", result);
    }

    #endregion

    #region Denormalize — 前缀还原

    [Fact]
    public void Denormalize_ExtendedDosPath_ShouldRemovePrefix()
    {
        // Arrange
        const string input = @"\\?\C:\Windows\System32\notepad.exe";

        // Act
        var result = PathNormalizer.Denormalize(input);

        // Assert
        Assert.Equal(@"C:\Windows\System32\notepad.exe", result);
    }

    [Fact]
    public void Denormalize_ExtendedUncPath_ShouldRestoreOriginalUncFormat()
    {
        // Arrange
        const string input = @"\\?\UNC\server\share\folder";

        // Act
        var result = PathNormalizer.Denormalize(input);

        // Assert
        Assert.Equal(@"\\server\share\folder", result);
    }

    [Fact]
    public void Denormalize_RegularPath_ShouldReturnUnchanged()
    {
        // Arrange
        const string input = @"C:\Users\Public\Documents";

        // Act
        var result = PathNormalizer.Denormalize(input);

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void Denormalize_UncPath_ShouldReturnUnchanged()
    {
        // Arrange
        const string input = @"\\localhost\shared\file.zip";

        // Act
        var result = PathNormalizer.Denormalize(input);

        // Assert — 普通 UNC 路径无扩展前缀，原样返回
        Assert.Equal(input, result);
    }

    [Fact]
    public void Denormalize_NullPath_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() => PathNormalizer.Denormalize(null!));
        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void Denormalize_EmptyPath_ShouldThrowArgumentException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => PathNormalizer.Denormalize(""));
        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void Denormalize_WhitespacePath_ShouldThrowArgumentException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => PathNormalizer.Denormalize("   "));
        Assert.Equal("path", ex.ParamName);
    }

    #endregion

    #region IsNormalized — 状态判断

    [Theory]
    [InlineData(@"\\?\C:\Windows", true)]
    [InlineData(@"\\?\D:\", true)]
    [InlineData(@"\\?\UNC\server\share", true)]
    [InlineData(@"\\?\Volume{guid}\folder", true)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"\\.\C:\device", false)]
    [InlineData(@"D:\Temp\file.log", false)]
    public void IsNormalized_ShouldDetectCorrectly(string path, bool expected)
    {
        // Act
        var result = PathNormalizer.IsNormalized(path);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsNormalized_NullPath_ShouldReturnFalse()
    {
        // Act
        var result = PathNormalizer.IsNormalized(null);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsNormalized_EmptyPath_ShouldReturnFalse()
    {
        // Act
        var result = PathNormalizer.IsNormalized("");

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Validate — 非法字符检查

    [Fact]
    public void Validate_ValidPath_ShouldNotThrow()
    {
        // Arrange
        const string input = @"C:\Program Files\MyApp\data\config.json";

        // Act — 不应抛出异常
        var exception = Record.Exception(() => PathNormalizer.Validate(input));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_ExtendedPath_ShouldNotThrow()
    {
        // Arrange
        const string input = @"\\?\C:\Very\Long\Path\file.dat";

        // Act
        var exception = Record.Exception(() => PathNormalizer.Validate(input));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_UncPath_ShouldNotThrow()
    {
        // Arrange
        const string input = @"\\server\share\subdir\file.txt";

        // Act
        var exception = Record.Exception(() => PathNormalizer.Validate(input));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_NullPath_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() => PathNormalizer.Validate(null!));
        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void Validate_EmptyPath_ShouldThrowArgumentException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => PathNormalizer.Validate(""));
        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void Validate_WhitespacePath_ShouldThrowArgumentException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => PathNormalizer.Validate("   "));
        Assert.Equal("path", ex.ParamName);
    }

    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('"')]
    [InlineData('|')]
    [InlineData('?')]
    [InlineData('*')]
    public void Validate_InvalidFileNameChar_ShouldThrowArgumentException(char invalidChar)
    {
        // Arrange
        var path = $@"C:\Windows\System32\file{invalidChar}name.dll";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => PathNormalizer.Validate(path));
        Assert.Contains($"'{invalidChar}'", ex.Message);
        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void Validate_ControlChar_ShouldThrowArgumentException()
    {
        // Arrange — 路径中包含 NUL 字符（ASCII 0）
        var path = "C:\\Windows\\System32\0hidden.dll";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => PathNormalizer.Validate(path));
        Assert.Contains("控制字符", ex.Message);
        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public void Validate_TabChar_ShouldThrowArgumentException()
    {
        // Arrange — 路径中包含 TAB（ASCII 9）控制字符
        var path = "C:\\Windows\\\tSystem32\\file.dll";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => PathNormalizer.Validate(path));
    }

    [Fact]
    public void Validate_NewlineChar_ShouldThrowArgumentException()
    {
        // Arrange — 路径中包含换行符
        var path = "C:\\Windows\\System32\nfile.dll";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => PathNormalizer.Validate(path));
    }

    #endregion

    #region 往返一致性（Roundtrip）

    [Fact]
    public void Roundtrip_NormalizeThenDenormalize_ShouldReturnOriginalPath()
    {
        // Arrange
        const string original = @"D:\Projects\MyApp\src\main.cs";

        // Act
        var normalized = PathNormalizer.Normalize(original);
        var denormalized = PathNormalizer.Denormalize(normalized);

        // Assert
        Assert.Equal(original, denormalized);
    }

    [Fact]
    public void Roundtrip_UncPath_ShouldReturnOriginalPath()
    {
        // Arrange
        const string original = @"\\nas\media\movies\action.mkv";

        // Act
        var normalized = PathNormalizer.Normalize(original);
        var denormalized = PathNormalizer.Denormalize(normalized);

        // Assert
        Assert.Equal(original, denormalized);
    }

    [Fact]
    public void Roundtrip_AlreadyNormalized_ShouldStayConsistent()
    {
        // Arrange
        const string original = @"\\?\E:\Data\backup.zip";

        // Act
        var normalized = PathNormalizer.Normalize(original);
        var denormalized = PathNormalizer.Denormalize(normalized);

        // Assert
        Assert.Equal(@"E:\Data\backup.zip", denormalized);
    }

    #endregion

    #region 边界与特殊情况

    [Fact]
    public void Normalize_VeryLongPath_ShouldSucceed()
    {
        // Arrange — 构造一个超过 MAX_PATH(260) 的路径
        // "subfolder\" = 10 字符, 26 层 = 260 + "C:\"(3) + "file.log"(8) = 271 > 260
        var deepFolder = string.Join("\\", Enumerable.Repeat("subfolder", 26));
        var longPath = $@"C:\{deepFolder}\file.log";

        // Act
        var result = PathNormalizer.Normalize(longPath);

        // Assert
        Assert.StartsWith(@"\\?\", result);
        Assert.True(result.Length > 260); // 确认超过 MAX_PATH
    }

    [Fact]
    public void Normalize_NonEnglishDriveLetter_ShouldWork()
    {
        // Arrange — 虽不常见，但 Windows 支持非 ASCII 驱动器号映射
        // 此处验证驱动器号检测逻辑的正确性（仅字母）
        const string input = @"Z:\Backup\archive.tar.gz";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert
        Assert.Equal(@"\\?\Z:\Backup\archive.tar.gz", result);
    }

    [Fact]
    public void Normalize_LowercaseDriveLetter_ShouldPreserveCase()
    {
        // Arrange
        const string input = @"c:\users\public";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert — 保留原始大小写，不做转换
        Assert.Equal(@"\\?\c:\users\public", result);
    }

    [Fact]
    public void IsNormalized_ShortString_ShouldReturnFalse()
    {
        // "C:" 长度不足 4 的字符串，不可能带 \\?\ 前缀
        Assert.False(PathNormalizer.IsNormalized("C:"));
        Assert.False(PathNormalizer.IsNormalized(@"\\.\"));
        Assert.False(PathNormalizer.IsNormalized("\\\\"));
    }

    [Fact]
    public void Validate_ChineseCharactersInPath_ShouldNotThrow()
    {
        // Arrange — 路径中包含中文字符（Unicode 合法）
        const string input = @"C:\用户\文档\项目\设计文档.md";

        // Act
        var exception = Record.Exception(() => PathNormalizer.Validate(input));

        // Assert — 中文字符不属于非法字符，应通过
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_EmojiInPath_ShouldNotThrowByDefault()
    {
        // Arrange — NTFS 实际允许 emoji 在文件名中，但极少使用
        const string input = "C:\\Temp\\report📊.xlsx";

        // Act
        var exception = Record.Exception(() => PathNormalizer.Validate(input));

        // Assert — emoji 不是被禁止的 NTFS 字符
        Assert.Null(exception);
    }

    [Fact]
    public void Normalize_PathWithOnlyDriveLetterNoBackslash_ShouldAddPrefix()
    {
        // Arrange — "C:" 是合法的绝对路径（相对于当前目录的 C 盘根目录）
        const string input = @"C:";

        // Act — 包含驱动器号字母 + 冒号，视为绝对路径格式
        var result = PathNormalizer.Normalize(input);

        // Assert
        Assert.Equal(@"\\?\C:", result);
    }

    #endregion
}
