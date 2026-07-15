// ============================================================
// EverythingFactAttribute.cs
// 功能: 自定义 xUnit [Fact] 属性——当 Everything SDK 不可用时自动跳过
// 替代 [Fact] + [Trait("Requires", "Everything")] + SkipIfUnavailable() 模式
// ============================================================

using Xunit;

namespace DirectoryCleanAgent.Tests.Infrastructure;

/// <summary>
/// 标记一个需要 Everything SDK 运行环境的测试方法。
/// 测试发现时若 Everything 不可用，自动设置 Skip 原因，测试被标记为 Skipped 而非 Failed。
///
/// 用法：
///   [EverythingFact]
///   public async Task MyTest() { ... }
///
/// 无需再调用 EverythingTestHelper.SkipIfUnavailable()。
/// </summary>
public class EverythingFactAttribute : FactAttribute
{
    public EverythingFactAttribute()
    {
        if (!EverythingTestHelper.IsAvailable)
        {
            Skip = $"Everything 环境不可用 ({EverythingTestHelper.Version ?? "未知"})";
        }
    }
}
