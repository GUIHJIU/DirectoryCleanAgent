using System.Text.RegularExpressions;

namespace DirectoryCleanAgent.Rules.Models;

/// <summary>
/// 预编译的 Glob 模式匹配器 — 在规则加载时一次性编译正则表达式，避免每次文件评估时重复编译。
///
/// 支持的 Glob 语法：
/// - ** 匹配零或多个目录层级（通过分段匹配实现）
/// - *  匹配零或多个文件名/目录名字符（不包括路径分隔符）
/// - ?  匹配单个字符（不包括路径分隔符）
///
/// 所有匹配大小写不敏感（Windows 文件系统特性）。
/// 使用 RegexOptions.Compiled 将正则编译为 IL，性能接近手写匹配代码。
/// </summary>
public sealed class CompiledGlobPattern
{
    private readonly string _pattern;

    // 不含 ** 的简单模式 → 单个预编译正则
    private readonly Regex? _simpleRegex;

    // 含 ** 的复杂模式 → 按 ** 分割后的各段正则（首段锚定开头，末段锚定结尾）
    private readonly CompiledDoubleStarSegment[]? _doubleStarSegments;

    // 是否以 ** 开头/结尾（用于判断锚定行为）
    private readonly bool _startsWithDoubleStar;
    private readonly bool _endsWithDoubleStar;

    public CompiledGlobPattern(string pattern)
    {
        _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        var pat = pattern.ToLowerInvariant();

        if (pat.Contains("**"))
        {
            _startsWithDoubleStar = pat.StartsWith("**");
            _endsWithDoubleStar = pat.EndsWith("**");
            _doubleStarSegments = CompileDoubleStarSegments(pat);
        }
        else if (pat.Contains('*') || pat.Contains('?'))
        {
            _simpleRegex = CompileRegex(GlobToRegex(pat));
        }
        // 无通配符 → _simpleRegex 和 _doubleStarSegments 均为 null，使用路径组件匹配
    }

    /// <summary>
    /// 检查路径是否匹配此 Glob 模式。
    /// 此方法在流式处理的每条文件上调用，必须高效。
    /// </summary>
    public bool IsMatch(string filePath)
    {
        var path = filePath.ToLowerInvariant();

        if (_simpleRegex != null)
        {
            return _simpleRegex.IsMatch(path);
        }

        if (_doubleStarSegments != null)
        {
            return MatchDoubleStar(path);
        }

        // 无通配符：作为完整路径组件精确匹配（前后必须是 \ 或字符串边界）
        var patternLower = _pattern.ToLowerInvariant();
        int searchStart = 0;
        return TryMatchPathComponent(path, patternLower, ref searchStart);
    }

    // ============================================================
    // Double-star 分段匹配
    // ============================================================

    private bool MatchDoubleStar(string path)
    {
        var segments = _doubleStarSegments!;
        int searchStart = 0;

        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            bool isFirst = (i == 0);
            bool isLast = (i == segments.Length - 1);

            // 判断锚定行为：
            // - 首段锚定开头：仅当模式不以 ** 开头时
            // - 末段锚定结尾：仅当模式不以 ** 结尾时
            bool anchorStart = isFirst && !_startsWithDoubleStar;
            bool anchorEnd = isLast && !_endsWithDoubleStar;

            if (anchorStart)
            {
                // 首段锚定：必须从路径开头匹配
                if (seg.Regex != null)
                {
                    var match = seg.Regex.Match(path);
                    if (!match.Success || match.Index != 0)
                        return false;
                    searchStart = match.Length;
                }
                else
                {
                    // 无通配符首段：必须以该文本开头，且后接路径分隔符或字符串结尾
                    if (!path.StartsWith(seg.Literal, StringComparison.Ordinal))
                        return false;
                    int afterLiteral = seg.Literal.Length;
                    if (afterLiteral < path.Length && path[afterLiteral] != '\\')
                        return false;
                    searchStart = afterLiteral;
                }
            }
            else if (anchorEnd)
            {
                // 末段锚定：必须匹配到路径结尾
                if (seg.Regex != null)
                {
                    var match = seg.Regex.Match(path, searchStart);
                    if (!match.Success)
                        return false;
                    if (match.Index + match.Length != path.Length)
                    {
                        match = seg.Regex.Match(path, path.Length - 1);
                        bool foundAtEnd = false;
                        while (match.Success)
                        {
                            if (match.Index + match.Length == path.Length)
                            {
                                foundAtEnd = true;
                                break;
                            }
                            match = match.NextMatch();
                        }
                        if (!foundAtEnd)
                            return false;
                    }
                }
                else
                {
                    // 无通配符末段：路径必须以该文本结尾，且前接路径分隔符或字符串开头
                    if (!path.EndsWith(seg.Literal, StringComparison.Ordinal))
                        return false;
                    int literalStart = path.Length - seg.Literal.Length;
                    if (literalStart > 0 && path[literalStart - 1] != '\\')
                        return false;
                }
            }
            else
            {
                // 非锚定段（被 ** 包围或中间段）：在 searchStart 之后找到，作为完整路径组件
                if (seg.Regex != null)
                {
                    var match = seg.Regex.Match(path, searchStart);
                    if (!match.Success)
                        return false;
                    searchStart = match.Index + match.Length;
                }
                else
                {
                    if (!TryMatchPathComponent(path, seg.Literal, ref searchStart))
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 在路径中查找字面量作为完整路径组件（前后必须是 \ 或字符串边界）。
    /// 从 searchStart 位置开始搜索，找到后更新 searchStart 为匹配结束位置。
    /// </summary>
    /// <returns>true 表示找到完整的路径组件匹配</returns>
    private static bool TryMatchPathComponent(string path, string literal, ref int searchStart)
    {
        int idx = searchStart;
        while (true)
        {
            idx = path.IndexOf(literal, idx, StringComparison.Ordinal);
            if (idx < 0)
                return false;

            // 验证匹配位置前是路径分隔符或字符串开头
            bool validStart = (idx == 0 || path[idx - 1] == '\\');
            // 验证匹配位置后是路径分隔符或字符串结尾
            int afterIdx = idx + literal.Length;
            bool validEnd = (afterIdx == path.Length || path[afterIdx] == '\\');

            if (validStart && validEnd)
            {
                searchStart = afterIdx;
                return true;
            }
            idx++; // 继续搜索下一个位置
        }
    }

    /// <summary>
    /// 将 ** 分割的模式编译为各段的正则。
    /// 首段锚定开头(^)，末段锚定结尾($)，中间段不锚定。
    /// </summary>
    private static CompiledDoubleStarSegment[] CompileDoubleStarSegments(string pattern)
    {
        var parts = pattern.Split(new[] { "**" }, StringSplitOptions.None);
        var segments = new List<CompiledDoubleStarSegment>();

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim('\\', '/');
            if (part.Length == 0)
                continue;

            if (part.Contains('*') || part.Contains('?'))
            {
                // 首段加 ^ 锚定，末段加 $ 锚定
                string regexStr;
                if (i == 0)
                    regexStr = "^" + GlobToRegex(part);
                else if (i == parts.Length - 1)
                    regexStr = GlobToRegex(part) + "$";
                else
                    regexStr = GlobToRegex(part);

                segments.Add(new CompiledDoubleStarSegment { Regex = CompileRegex(regexStr) });
            }
            else
            {
                segments.Add(new CompiledDoubleStarSegment { Literal = part });
            }
        }

        return segments.ToArray();
    }

    // ============================================================
    // Glob → Regex 转换
    // ============================================================

    /// <summary>
    /// 将简化的 Glob 模式转换为正则表达式字符串。
    /// * → .*（任意字符），? → .（单个字符），路径分隔符和正则元字符转义。
    /// 调用方负责添加行首/行尾锚定（^/$），此处仅转换模式体。
    /// </summary>
    private static string GlobToRegex(string glob)
    {
        // 预分配容量：每个字符最多转义为 2 个字符（如 \\.）
        var sb = new System.Text.StringBuilder(glob.Length * 2);

        foreach (var ch in glob)
        {
            switch (ch)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '.':
                    sb.Append("\\.");
                    break;
                case '^':
                case '$':
                case '(':
                case ')':
                case '[':
                case ']':
                case '{':
                case '}':
                case '|':
                case '+':
                    sb.Append('\\');
                    sb.Append(ch);
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 编译正则表达式（IgnoreCase + Compiled + CultureInvariant）。
    /// Compiled 将正则编译为 IL，虽首次编译较慢（规则加载时），
    /// 但执行速度接近手写代码，适合百万级文件的热路径匹配。
    /// </summary>
    private static Regex CompileRegex(string pattern)
    {
        return new Regex(pattern,
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled |
            RegexOptions.CultureInvariant |
            RegexOptions.Singleline);
    }

    // ============================================================
    // 内部类型
    // ============================================================

    private sealed class CompiledDoubleStarSegment
    {
        /// <summary>预编译正则（含通配符段），null 表示纯文本段</summary>
        public Regex? Regex { get; init; }

        /// <summary>纯文本字面量（无通配符段），空字符串表示不存在</summary>
        public string Literal { get; init; } = string.Empty;
    }
}
