namespace DirectoryCleanAgent.Core.PathHandling;

/// <summary>
/// 路径标准化工具。
/// 将所有内部路径统一转换为 \\?\ 扩展长度格式，支持超长路径（突破 MAX_PATH 260 字符限制）。
///
/// 转换规则（与设计文档 10 节一致）：
/// - 本地绝对路径 C:\Dir\File → \\?\C:\Dir\File
/// - UNC 路径 \\Server\Share\File → \\?\UNC\Server\Share\File
/// - 已标准化的路径 → 幂等返回
/// - 相对路径 → 抛出 ArgumentException
///
/// \\?\ 前缀下的关键行为：
/// - Windows 不会对路径进行规范化（保留尾部空格和点号）
/// - 路径长度限制从 MAX_PATH(260) 提升到 32767 字符
/// - System.IO 所有文件 API 原生支持此格式
///
/// 不可修改性约束：本工具产生的标准化路径仅用于内部 API 调用，
/// 不应持久化到数据库（数据库存储使用原始路径格式）。
/// </summary>
public static class PathNormalizer
{
    /// <summary>\\?\ 扩展路径前缀</summary>
    public const string ExtendedPathPrefix = @"\\?\";

    /// <summary>UNC 扩展路径前缀（\\?\UNC\）</summary>
    public const string UncExtendedPrefix = @"\\?\UNC\";

    /// <summary>普通 UNC 路径前缀（\\）</summary>
    public const string UncPrefix = @"\\";

    /// <summary>Win32 设备命名空间前缀（\\.\）</summary>
    private const string DevicePrefix = @"\\.\";

    /// <summary>扩展路径允许的最大字符数（含前缀和终止符）</summary>
    public const int MaxExtendedPathLength = 32767;

    /// <summary>\\?\ 前缀的长度（4 个字符）</summary>
    private const int ExtendedPrefixLength = 4;

    /// <summary>
    /// 将路径标准化为 \\?\ 扩展格式。
    ///
    /// 转换逻辑：
    /// 1. 已带 \\?\ 前缀 → 直接返回（幂等）
    /// 2. 带 \\.\ 设备前缀 → 将 \\.\ 替换为 \\?\ 后返回
    /// 3. UNC 路径（\\server\share...）→ 转为 \\?\UNC\server\share...
    /// 4. 绝对路径（C:\...）→ 转为 \\?\C:\...
    /// 5. 相对路径 → 抛出 ArgumentException
    /// </summary>
    /// <param name="path">待标准化的路径</param>
    /// <returns>带 \\?\ 前缀的扩展路径</returns>
    /// <exception cref="ArgumentNullException">path 为 null</exception>
    /// <exception cref="ArgumentException">path 为空字符串、纯空白或相对路径</exception>
    public static string Normalize(string path)
    {
        // 参数校验：null 检查
        if (path is null)
            throw new ArgumentNullException(nameof(path), "路径不能为 null。");

        // 参数校验：空或纯空白检查
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("路径不能为空或纯空白。", nameof(path));

        // 场景1：已是 \\?\ 扩展格式，幂等返回
        if (path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal))
            return path;

        // 场景2：是 \\.\ 设备命名空间格式，替换前缀为 \\?\
        if (path.StartsWith(DevicePrefix, StringComparison.Ordinal))
            return ExtendedPathPrefix + path[DevicePrefix.Length..];

        // 场景3：UNC 路径（以 \\ 开头，但不是扩展路径前缀）
        if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            // \\server\share... → \\?\UNC\server\share...
            // 去掉开头的两个反斜杠，加上 \\?\UNC\ 前缀
            return UncExtendedPrefix + path[UncPrefix.Length..];
        }

        // 场景4：检查是否为绝对路径（如 C:\ 或 D:\）
        // 在 Windows 上，绝对路径格式为 [驱动器号]:\[路径]
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            return ExtendedPathPrefix + path;
        }

        // 场景5：无法识别的路径格式（相对路径等）
        throw new ArgumentException(
            $"路径格式不支持，仅接受绝对路径或 UNC 路径: '{path}'。相对路径无法标准化为 \\\\?\\ 格式。",
            nameof(path));
    }

    /// <summary>
    /// 移除 \\?\ 前缀，还原为用户可读的展示路径。
    ///
    /// 还原规则：
    /// - \\?\C:\... → C:\...
    /// - \\?\UNC\server\share... → \\server\share...
    /// - 无扩展前缀的路径 → 原样返回
    /// </summary>
    /// <param name="path">带 \\?\ 前缀的路径</param>
    /// <returns>去除前缀后的标准路径</returns>
    /// <exception cref="ArgumentNullException">path 为 null</exception>
    /// <exception cref="ArgumentException">path 为空字符串或纯空白</exception>
    public static string Denormalize(string path)
    {
        // 参数校验：null 检查
        if (path is null)
            throw new ArgumentNullException(nameof(path), "路径不能为 null。");

        // 参数校验：空或纯空白检查
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("路径不能为空或纯空白。", nameof(path));

        // 场景1：\\?\UNC\ 前缀 → 还原为 \\server\share...
        if (path.StartsWith(UncExtendedPrefix, StringComparison.Ordinal))
            return UncPrefix + path[UncExtendedPrefix.Length..];

        // 场景2：\\?\ 前缀但不是 UNC 扩展 → 直接去掉 \\?\ 前缀
        if (path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal))
            return path[ExtendedPrefixLength..];

        // 场景3：无扩展前缀，原样返回
        return path;
    }

    /// <summary>
    /// 判断路径是否已带 \\?\ 扩展前缀。
    /// 用于在拼接路径等操作前快速判断是否需要标准化。
    /// </summary>
    /// <param name="path">待检查的路径</param>
    /// <returns>true 表示已带扩展前缀，false 表示未带或参数为 null/空</returns>
    public static bool IsNormalized(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        return path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证路径是否包含非法字符。
    ///
    /// 即使在 \\?\ 扩展路径下，以下字符仍被 NTFS 禁止：
    /// - 控制字符（ASCII 0-31）
    /// - &lt; &gt; " | ? *
    ///
    /// 仅检查文件名中不可用的字符，不检查路径长度（由调用方根据需要判断）。
    /// </summary>
    /// <param name="path">待验证的路径</param>
    /// <exception cref="ArgumentNullException">path 为 null</exception>
    /// <exception cref="ArgumentException">path 为空字符串或包含非法字符</exception>
    public static void Validate(string path)
    {
        // 参数校验：null 检查
        if (path is null)
            throw new ArgumentNullException(nameof(path), "路径不能为 null。");

        // 参数校验：空或纯空白检查
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("路径不能为空或纯空白。", nameof(path));

        // 跳过扩展路径前缀，避免将 \\?\ 中的 ? 误判为非法字符
        // \\?\ → 跳过 4 个字符；\\?\UNC\ → 跳过 8 个字符；\\.\ → 跳过 4 个字符
        var startIndex = 0;
        if (path.StartsWith(UncExtendedPrefix, StringComparison.Ordinal))
            startIndex = UncExtendedPrefix.Length;
        else if (path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal))
            startIndex = ExtendedPrefixLength;
        else if (path.StartsWith(DevicePrefix, StringComparison.Ordinal))
            startIndex = DevicePrefix.Length;

        // 逐字符检查非法字符
        for (var i = startIndex; i < path.Length; i++)
        {
            var ch = path[i];

            // 检查控制字符（ASCII 0-31，含 NUL）
            if (ch <= '\u001F')
            {
                throw new ArgumentException(
                    $"路径中包含非法控制字符（ASCII {(int)ch}），位置: {i}。",
                    nameof(path));
            }

            // 检查 NTFS 禁止的文件名字符
            // 即使在 \\?\ 前缀下，这些字符也不允许出现在文件名中
            switch (ch)
            {
                case '<':
                case '>':
                case '"':
                case '|':
                case '?':
                case '*':
                    throw new ArgumentException(
                        $"路径中包含非法字符 '{ch}'，位置: {i}。",
                        nameof(path));
            }
        }

        // 额外检查：路径不应包含 NUL 字符（\0）
        // 已在控制字符检查中覆盖，此处补充提示
        if (path.Contains('\0'))
        {
            throw new ArgumentException(
                "路径中包含 NUL 字符，该字符在任何 Windows 文件系统中均不被允许。",
                nameof(path));
        }
    }
}
