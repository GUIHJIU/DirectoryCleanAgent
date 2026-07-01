// ============================================================
// DirectoryStructureGenerator.cs
// 功能: 根据分布配置预创建目录树结构
// 在并行文件写入前完成，避免 Directory.CreateDirectory 竞争
// ============================================================

namespace DirectoryCleanAgent.Tools.TestVolumeGenerator;

/// <summary>
/// 目录结构生成器 — 在文件写入前批量创建所有目录
/// 避免并行写入时的 Directory.CreateDirectory 线程竞争
/// </summary>
public static class DirectoryStructureGenerator
{
    /// <summary>
    /// 根据目录节点列表创建完整的目录树
    /// 返回 (目录绝对路径, 节点配置) 的映射列表，供后续文件生成使用
    /// </summary>
    /// <param name="rootPath">卷根目录</param>
    /// <param name="nodes">目录节点配置</param>
    /// <returns>目录路径与对应节点的列表</returns>
    public static List<(string FullPath, DirectoryNode Node)> CreateDirectories(
        string rootPath,
        DirectoryNode[] nodes)
    {
        var result = new List<(string, DirectoryNode)>(nodes.Length);

        // 先创建根目录
        if (!Directory.Exists(rootPath))
        {
            Directory.CreateDirectory(rootPath);
            Console.WriteLine($"[目录] 创建根目录: {rootPath}");
        }

        // 依次创建所有子目录
        foreach (var node in nodes)
        {
            try
            {
                var fullPath = Path.Combine(rootPath, node.RelativePath);

                // 规范化路径分隔符
                fullPath = Path.GetFullPath(fullPath);

                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }

                result.Add((fullPath, node));
                Console.WriteLine($"[目录] 已就绪: {node.RelativePath} (占比={node.Ratio:P0}, 扩展名=[{string.Join(", ", node.Extensions)}])");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[目录错误] 创建目录失败: {node.RelativePath} — {ex.Message}");
                throw; // 目录创建失败是致命错误，不能继续
            }
        }

        return result;
    }
}
