using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Everything.Interop;

namespace DirectoryCleanAgent.Everything;

/// <summary>
/// Everything 排序映射器。
///
/// 将 UI 层的 EverythingSortType 枚举映射为 Everything SDK 原生排序常量。
/// 映射关系对应设计文档补充说明 4.7 节的排序参数表。
///
/// 注意：语义标签排序无法委托给 Everything（标签是规则引擎产出），
/// 仅对当前 FileDecisionCache 子集进行内存排序。
/// </summary>
internal static class EverythingSortMapper
{
    /// <summary>
    /// 将 EverythingSortType 枚举 + 排序方向映射为 Everything 原生排序常量。
    /// </summary>
    /// <param name="sortType">排序字段，null 表示使用 Everything 默认排序</param>
    /// <param name="descending">true 为降序，false 为升序</param>
    /// <returns>Everything 原生排序常量，0 表示默认排序</returns>
    internal static uint Map(EverythingSortType? sortType, bool descending)
    {
        if (sortType is null)
        {
            return 0; // Everything 默认排序（通常按名称升序）
        }

        return (sortType.Value, descending) switch
        {
            // 按文件名排序
            (EverythingSortType.Name, false) => EverythingNative.SORT_NAME_ASCENDING,
            (EverythingSortType.Name, true)  => EverythingNative.SORT_NAME_DESCENDING,

            // 按路径排序
            (EverythingSortType.Path, false) => EverythingNative.SORT_PATH_ASCENDING,
            (EverythingSortType.Path, true)  => EverythingNative.SORT_PATH_DESCENDING,

            // 按文件大小排序
            (EverythingSortType.Size, false) => EverythingNative.SORT_SIZE_ASCENDING,
            (EverythingSortType.Size, true)  => EverythingNative.SORT_SIZE_DESCENDING,

            // 按扩展名排序
            (EverythingSortType.Extension, false) => EverythingNative.SORT_EXTENSION_ASCENDING,
            (EverythingSortType.Extension, true)  => EverythingNative.SORT_EXTENSION_DESCENDING,

            // 按修改日期排序
            (EverythingSortType.DateModified, false) => EverythingNative.SORT_DATE_MODIFIED_ASCENDING,
            (EverythingSortType.DateModified, true)  => EverythingNative.SORT_DATE_MODIFIED_DESCENDING,

            _ => 0
        };
    }
}
