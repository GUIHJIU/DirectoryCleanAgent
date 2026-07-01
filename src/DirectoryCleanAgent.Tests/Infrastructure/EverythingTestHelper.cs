// ============================================================
// EverythingTestHelper.cs
// 功能: Everything SDK 运行时可检测辅助类
// 在集成测试/性能测试中判断 Everything 是否可用
// 避免在无 Everything 环境下运行相关测试导致崩溃
// ============================================================

using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.Infrastructure;

/// <summary>
/// Everything SDK 运行时可检测辅助类
/// 提供静态方法检测 Everything 的可用性，供集成测试和性能测试使用
/// </summary>
public static class EverythingTestHelper
{
    private static bool? _cachedAvailability;
    private static string? _cachedVersion;
    private static bool? _cachedFrnAvailable;
    private static readonly object LockObj = new();

    /// <summary>Everything 是否可用（服务运行 + IPC 正常 + 版本合格）</summary>
    public static bool IsAvailable => GetAvailability().isAvailable;

    /// <summary>Everything 版本字符串</summary>
    public static string? Version => GetAvailability().version;

    /// <summary>FRN 能力是否可用</summary>
    public static bool IsFrnAvailable => GetAvailability().frnAvailable;

    /// <summary>
    /// 检测 Everything 环境可用性（结果缓存，首次调用后复用）
    /// 返回 (isAvailable, version, frnAvailable)
    /// </summary>
    private static (bool isAvailable, string? version, bool frnAvailable) GetAvailability()
    {
        if (_cachedAvailability.HasValue)
            return (_cachedAvailability.Value, _cachedVersion, _cachedFrnAvailable ?? false);

        lock (LockObj)
        {
            // 双重检查
            if (_cachedAvailability.HasValue)
                return (_cachedAvailability.Value, _cachedVersion, _cachedFrnAvailable ?? false);

            try
            {
                // 策略 1: 检查 Everything 进程是否存在
                var processes = System.Diagnostics.Process.GetProcessesByName("Everything");
                if (processes.Length == 0)
                {
                    _cachedAvailability = false;
                    _cachedVersion = "进程未运行";
                    _cachedFrnAvailable = false;
                    return (false, _cachedVersion, false);
                }

                // 策略 2: 尝试通过 Everything64.dll P/Invoke 获取版本
                // 使用反射避免硬依赖 Everything 项目（Everything SDK 的互操作 DLL 可能不存在）
                try
                {
                    uint majorVersion = NativeMethods.Everything_GetMajorVersion();
                    if (majorVersion == 0)
                    {
                        _cachedAvailability = false;
                        _cachedVersion = "无法获取版本（Everything64.dll 不可用）";
                        _cachedFrnAvailable = false;
                    }
                    else
                    {
                        _cachedAvailability = true;
                        _cachedVersion = $"{majorVersion}.x.x.x";
                        _cachedFrnAvailable = true; // 假设 FRN 可用（可通过 Everything SDK 接口进一步验证）
                    }
                }
                catch (DllNotFoundException)
                {
                    _cachedAvailability = false;
                    _cachedVersion = "Everything64.dll 未找到";
                    _cachedFrnAvailable = false;
                }
                catch (Exception ex)
                {
                    _cachedAvailability = false;
                    _cachedVersion = $"检测异常: {ex.Message}";
                    _cachedFrnAvailable = false;
                }
            }
            catch (Exception ex)
            {
                _cachedAvailability = false;
                _cachedVersion = $"环境异常: {ex.Message}";
                _cachedFrnAvailable = false;
            }

            return (_cachedAvailability.Value, _cachedVersion, _cachedFrnAvailable ?? false);
        }
    }

    /// <summary>重置缓存状态（用于测试 Everything 安装后的重新检测）</summary>
    public static void ResetCache()
    {
        lock (LockObj)
        {
            _cachedAvailability = null;
            _cachedVersion = null;
            _cachedFrnAvailable = null;
        }
    }

    /// <summary>
    /// 在集成测试中跳过因 Everything 不可用而无法执行的测试
    /// 用法: if (!EverythingTestHelper.IsAvailable) return; // 或使用 Skip
    /// </summary>
    public static void SkipIfUnavailable(ILogger? logger = null)
    {
        if (!IsAvailable)
        {
            var msg = $"Everything 环境不可用 ({Version})，跳过此测试";
            logger?.LogWarning(msg);
            throw new SkipException(msg);
        }
    }

    // ============================================================
    // 内部 P/Invoke 定义（仅用于运行时探测，不依赖 Everything 项目）
    // ============================================================
    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("Everything64.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint Everything_GetMajorVersion();
    }
}

/// <summary>
/// xUnit Skip 异常 — 不视为测试失败，标记为 Skipped
/// 注意: xUnit 本身不支持通过异常跳过，此处使用自定义机制。
/// 实际使用时建议通过 [Fact(Skip = "...")] 或条件编译控制。
/// 替代方案: 在测试方法开头使用 Assert.Skip (若可用) 或直接 return
/// </summary>
public class SkipException : Exception
{
    public SkipException(string message) : base(message) { }
}
