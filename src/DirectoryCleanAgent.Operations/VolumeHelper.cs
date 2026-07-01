using System.Runtime.InteropServices;
using DirectoryCleanAgent.Core.PathHandling;
using DirectoryCleanAgent.Operations.Interop;

namespace DirectoryCleanAgent.Operations;

/// <summary>
/// 卷信息工具类。
///
/// 提供以下功能：
/// - 判断两个路径是否位于同一卷（用于隔离区跨卷检测）
/// - 获取指定路径所在卷的序列号
/// - 从 \\?\ 前缀路径中提取卷根路径
/// </summary>
internal static class VolumeHelper
{
    /// <summary>
    /// 判断两个路径是否位于同一卷。
    ///
    /// 实现原理：
    /// 分别查询两个路径所在卷的序列号，若序列号相同则位于同一卷。
    /// 同一卷上的移动操作是原子的（仅修改文件系统元数据），
    /// 跨卷移动则必须复制数据。
    /// </summary>
    /// <param name="path1">\\?\ 格式的路径1</param>
    /// <param name="path2">\\?\ 格式的路径2</param>
    /// <returns>true 表示位于同一卷</returns>
    public static bool AreOnSameVolume(string path1, string path2)
    {
        var serial1 = GetVolumeSerialNumber(path1);
        var serial2 = GetVolumeSerialNumber(path2);

        // 任一查询失败，保守假定为跨卷（走复制流程更安全）
        if (serial1 == null || serial2 == null)
            return false;

        return serial1 == serial2;
    }

    /// <summary>
    /// 获取指定路径所在卷的序列号。
    /// </summary>
    /// <param name="path">\\?\ 格式的路径</param>
    /// <returns>卷序列号，查询失败返回 null</returns>
    public static uint? GetVolumeSerialNumber(string path)
    {
        try
        {
            var rootPath = GetVolumeRootPath(path);
            if (rootPath == null)
                return null;

            var result = Shell32Native.GetVolumeInformationW(
                rootPath,
                IntPtr.Zero, 0,
                out uint serialNumber,
                out _, out _,
                IntPtr.Zero, 0);

            return result ? serialNumber : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 \\?\ 前缀路径中提取卷根路径（如 "C:\"）。
    ///
    /// 转换规则：
    /// - \\?\C:\Dir\File → C:\
    /// - \\?\UNC\Server\Share\Dir → \\?\UNC\Server\Share\（UNC 整体作为卷根）
    /// </summary>
    /// <param name="path">\\?\ 格式的路径</param>
    /// <returns>卷根路径，解析失败返回 null</returns>
    internal static string? GetVolumeRootPath(string path)
    {
        // 去掉 \\?\ 前缀获取传统路径
        var denormalized = PathNormalizer.Denormalize(path);

        if (denormalized.Length >= 3 && denormalized[1] == ':' && denormalized[2] == '\\')
        {
            // 本地驱动器：C:\Dir\File → C:\
            return denormalized[..3];
        }

        if (denormalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC 路径：找到 share 之后的第一个反斜杠
            // \\server\share\dir → \\server\share\
            var thirdSlash = denormalized.IndexOf('\\', 2);
            if (thirdSlash > 0)
            {
                var fourthSlash = denormalized.IndexOf('\\', thirdSlash + 1);
                if (fourthSlash > 0)
                    return denormalized[..(fourthSlash + 1)];
                // 恰好只有 \\server\share 没有子目录
                return denormalized + "\\";
            }
        }

        return null;
    }
}
