using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Everything;
using DirectoryCleanAgent.Everything.Interop;

namespace DirectoryCleanAgent.Tests.Everything;

/// <summary>
/// EverythingSortMapper 单元测试。
///
/// 覆盖范围：
/// - 全部 5 个 EverythingSortType 枚举值 × 2 种排序方向（升序/降序）= 10 种映射
/// - null SortType → 0（默认排序）
/// - 每个映射验证返回的原生常量值正确
/// </summary>
public class EverythingSortMapperTests
{
    [Fact]
    public void Map_NullSortType_ReturnsZero()
    {
        // Act
        var result = EverythingSortMapper.Map(null, false);

        // Assert: 0 表示 Everything 默认排序
        Assert.Equal(0u, result);
    }

    [Fact]
    public void Map_NameAscending_ReturnsSortNameAscending()
    {
        var result = EverythingSortMapper.Map(EverythingSortType.Name, false);
        Assert.Equal(EverythingNative.SORT_NAME_ASCENDING, result);
    }

    [Fact]
    public void Map_NameDescending_ReturnsSortNameDescending()
    {
        var result = EverythingSortMapper.Map(EverythingSortType.Name, true);
        Assert.Equal(EverythingNative.SORT_NAME_DESCENDING, result);
    }

    [Fact]
    public void Map_PathAscending_ReturnsSortPathAscending()
    {
        var result = EverythingSortMapper.Map(EverythingSortType.Path, false);
        Assert.Equal(EverythingNative.SORT_PATH_ASCENDING, result);
    }

    [Fact]
    public void Map_PathDescending_ReturnsSortPathDescending()
    {
        var result = EverythingSortMapper.Map(EverythingSortType.Path, true);
        Assert.Equal(EverythingNative.SORT_PATH_DESCENDING, result);
    }

    [Fact]
    public void Map_SizeAscending_ReturnsSortSizeAscending()
    {
        var result = EverythingSortMapper.Map(EverythingSortType.Size, false);
        Assert.Equal(EverythingNative.SORT_SIZE_ASCENDING, result);
    }

    [Fact]
    public void Map_SizeDescending_ReturnsSortSizeDescending()
    {
        var result = EverythingSortMapper.Map(EverythingSortType.Size, true);
        Assert.Equal(EverythingNative.SORT_SIZE_DESCENDING, result);
    }

    [Fact]
    public void Map_ExtensionAscending_ReturnsSortExtensionAscending()
    {
        var result = EverythingSortMapper.Map(EverythingSortType.Extension, false);
        Assert.Equal(EverythingNative.SORT_EXTENSION_ASCENDING, result);
    }

    [Fact]
    public void Map_ExtensionDescending_ReturnsSortExtensionDescending()
    {
        var result = EverythingSortMapper.Map(EverythingSortType.Extension, true);
        Assert.Equal(EverythingNative.SORT_EXTENSION_DESCENDING, result);
    }

    [Fact]
    public void Map_DateModifiedAscending_ReturnsSortDateModifiedAscending()
    {
        var result = EverythingSortMapper.Map(EverythingSortType.DateModified, false);
        Assert.Equal(EverythingNative.SORT_DATE_MODIFIED_ASCENDING, result);
    }

    [Fact]
    public void Map_DateModifiedDescending_ReturnsSortDateModifiedDescending()
    {
        var result = EverythingSortMapper.Map(EverythingSortType.DateModified, true);
        Assert.Equal(EverythingNative.SORT_DATE_MODIFIED_DESCENDING, result);
    }

    /// <summary>
    /// 验证所有枚举值 + 排序方向组合的映射均为有效非零值（null 除外）。
    /// </summary>
    [Theory]
    [InlineData(EverythingSortType.Name, false, 1u)]
    [InlineData(EverythingSortType.Name, true, 2u)]
    [InlineData(EverythingSortType.Path, false, 3u)]
    [InlineData(EverythingSortType.Path, true, 4u)]
    [InlineData(EverythingSortType.Size, false, 5u)]
    [InlineData(EverythingSortType.Size, true, 6u)]
    [InlineData(EverythingSortType.Extension, false, 7u)]
    [InlineData(EverythingSortType.Extension, true, 8u)]
    [InlineData(EverythingSortType.DateModified, false, 11u)]
    [InlineData(EverythingSortType.DateModified, true, 12u)]
    public void Map_AllCombinations_ReturnExpectedConstant(
        EverythingSortType sortType, bool descending, uint expected)
    {
        var result = EverythingSortMapper.Map(sortType, descending);
        Assert.Equal(expected, result);
    }
}
