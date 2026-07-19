# Everything SDK 线程安全修复 — 设计方案

**日期**: 2026-07-19  
**方案**: B — 全面修复  
**状态**: 待审核

---

## 一、设计目标

1. 消除 Everything SDK v2.0 进程全局状态的并发竞争（根源：实例级锁 + xUnit 并行测试）
2. 消除 `Everything_SetMax(1)` 全局残留对后续查询的截断影响
3. 消除 `EverythingDependencyDetector` 静态方法绕过锁直接调用 `EverythingNative.*` 的风险
4. 确保所有 `[EverythingFact]` 集成测试串行执行，互不干扰

---

## 二、架构决策

### 2.1 核心决策：提取共享锁为独立类 `EverythingSdkLock`

**选择理由**：
- `EverythingDependencyDetector` 和 `EverythingFileListProvider` 均为 Singleton，DI 容器可提供同一锁实例
- 避免静态字段耦合（两个类不应通过 `internal static` 字段互相依赖）
- 遵循 DI 最佳实践，测试可替换锁行为

```csharp
// 新文件: EverythingSdkLock.cs
namespace DirectoryCleanAgent.Everything;

public sealed class EverythingSdkLock : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task WaitAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);
    public bool Wait(int millisecondsTimeout) => _semaphore.Wait(millisecondsTimeout);
    public void Release() => _semaphore.Release();
    public void Dispose() => _semaphore.Dispose();
}
```

### 2.2 IEverythingSdk 接口扩展

新增三个方法：

| 方法 | 签名 | 用途 |
|------|------|------|
| `GetMax()` | `uint GetMax()` | 保存当前 Max 值，用于 save/restore |
| `SetMax(uint)` | `void SetMax(uint max)` | 设置 Max 值（检测器限制为 1 条） |
| `ResetMax()` | `void ResetMax()` | 便捷重置为 0（无限制） |

### 2.3 SetMax 策略：save/restore

```
每次需要限制 Max=1 时：
  1. uint prevMax = _sdk.GetMax()     // 保存
  2. _sdk.SetMax(1)                    // 设置
  3. ... 执行搜索 ...
  4. finally: _sdk.SetMax(prevMax)     // 恢复
```

### 2.4 EverythingDependencyDetector 方法改造

| 方法 | 改造前 | 改造后 |
|------|--------|--------|
| `PingIpc()` | `internal static`，直接调 `EverythingNative.*` | `private`，通过 `_sdk.*` + 锁 + save/restore |
| `CheckVersion()` | `internal static`，直接调 `EverythingNative.*` | `private`，通过 `_sdk.*`（只读，无锁） |
| `DetectFrnCapability()` | `internal static`，直接调 `EverythingNative.*` | `private`，通过 `_sdk.*` + 锁 + save/restore |
| `CheckIndexReady()` | `internal static`，直接调 `EverythingNative.*` | `private`，通过 `_sdk.*` + 锁 + save/restore |
| `Cleanup()` | `public static` | 移除（DI 容器管理生命周期） |

---

## 三、文件变更清单（共 9 个文件）

### 新增文件

| # | 文件 | 说明 |
|---|------|------|
| 1 | `EverythingSdkLock.cs` | 共享锁类（~25 行） |
| 2 | `EverythingTestCollection.cs` | 测试串行化配置（~20 行） |

### 修改文件

| # | 文件 | 改动范围 |
|---|------|---------|
| 3 | `EverythingNative.cs` | 新增 `Everything_GetMax()` P/Invoke（+5 行） |
| 4 | `IEverythingSdk.cs` | 新增 `GetMax()`、`SetMax()`、`ResetMax()` 接口方法（+12 行） |
| 5 | `EverythingSdkWrapper.cs` | 实现 3 个新方法（+20 行） |
| 6 | `EverythingFileListProvider.cs` | 注入 `EverythingSdkLock` + EnumerateFilesAsync/PollForChanges 开头调 `ResetMax()`（~15 行变更） |
| 7 | `EverythingDependencyDetector.cs` | 构造函数新增锁参数 + 4 个方法改造 + Cleanup 移除（~100 行变更） |
| 8 | `ServiceRegistration.cs` | 注册 `EverythingSdkLock` 为 Singleton（+1 行） |
| 9 | `App.xaml.cs` | 移除 `EverythingDependencyDetector.Cleanup()` 调用（-2 行） |

### 测试文件修改（4 个文件）

| # | 文件 | 改动 |
|---|------|------|
| 10 | `ScanCancellationTests.cs` | 添加 `[Collection("Everything")]` |
| 11 | `EverythingScanIntegrationTests.cs` | 添加 `[Collection("Everything")]` |
| 12 | `EverythingDetectionIntegrationTests.cs` | 添加 `[Collection("Everything")]` |
| 13 | `IndexNotReadyIntegrationTests.cs` | 添加 `[Collection("Everything")]` |

---

## 四、关键代码设计

### 4.1 EverythingSdkLock（新文件）

```csharp
// src/DirectoryCleanAgent.Everything/EverythingSdkLock.cs
namespace DirectoryCleanAgent.Everything;

/// <summary>
/// Everything SDK 全局状态共享锁。
/// Everything SDK 2.0 使用进程全局状态，同一时刻只允许一个操作访问 SDK。
/// 由 EverythingFileListProvider 和 EverythingDependencyDetector 共享，
/// 通过 DI 容器注册为 Singleton。
/// </summary>
public sealed class EverythingSdkLock : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public Task WaitAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        return _semaphore.WaitAsync(ct);
    }

    public bool Wait(int millisecondsTimeout)
    {
        ThrowIfDisposed();
        return _semaphore.Wait(millisecondsTimeout);
    }

    public void Release()
    {
        _semaphore.Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _semaphore.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(EverythingSdkLock));
    }
}
```

### 4.2 IEverythingSdk 新增方法

```csharp
// 在 SetRequestFlags 之后新增：

/// <summary>获取当前 MaxResults 设置。0 表示无限制。</summary>
uint GetMax();

/// <summary>设置最大返回结果数。0 = 无限制。</summary>
void SetMax(uint max);

/// <summary>重置 MaxResults 为 0（无限制）。等价于 SetMax(0)。</summary>
void ResetMax() => SetMax(0);  // C# 8.0+ 接口默认实现
```

### 4.3 EverythingDependencyDetector 关键方法改造

#### PingIpc（改造后）

```csharp
private async Task<bool> PingIpcAsync(CancellationToken ct)
{
    await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        if (_sdk != null)
        {
            _sdk.SetSearch(IpcProbeFilePath);
            uint prevMax = _sdk.GetMax();
            _sdk.SetMax(1);
            try
            {
                if (!_sdk.Query(true)) return false;
                return _sdk.GetLastError() == EverythingNative.ERROR_OK
                       && _sdk.GetNumResults() > 0;
            }
            finally { _sdk.SetMax(prevMax); }
        }
        else
        {
            EverythingNative.Everything_SetSearch(IpcProbeFilePath);
            uint prevMax = EverythingNative.Everything_GetMax();
            EverythingNative.Everything_SetMax(1);
            try
            {
                if (!EverythingNative.Everything_Query(true)) return false;
                return EverythingNative.Everything_GetLastError()
                       == EverythingNative.ERROR_OK
                       && EverythingNative.Everything_GetNumResults() > 0;
            }
            finally { EverythingNative.Everything_SetMax(prevMax); }
        }
    }
    catch (Exception) { return false; }
    finally { _sdkLock.Release(); }
}
```

#### CheckIndexReady（改造后）

```csharp
private async Task<bool> CheckIndexReadyAsync(CancellationToken ct)
{
    await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        if (_sdk != null)
        {
            if (!_sdk.IsDBLoaded()) return false;
            _sdk.SetSearch(IpcProbeFilePath);
            uint prevMax = _sdk.GetMax();
            _sdk.SetMax(1);
            try
            {
                return _sdk.Query(true) && _sdk.GetNumResults() > 0;
            }
            finally { _sdk.SetMax(prevMax); }
        }
        else
        {
            if (!EverythingNative.Everything_IsDBLoaded()) return false;
            EverythingNative.Everything_SetSearch(IpcProbeFilePath);
            uint prevMax = EverythingNative.Everything_GetMax();
            EverythingNative.Everything_SetMax(1);
            try
            {
                return EverythingNative.Everything_Query(true)
                       && EverythingNative.Everything_GetNumResults() > 0;
            }
            finally { EverythingNative.Everything_SetMax(prevMax); }
        }
    }
    catch (Exception) { return false; }
    finally { _sdkLock.Release(); }
}
```

### 4.4 EnumerateFilesAsync 清除残留

```csharp
// 在 SetRequestFlags 之前新增：
_sdk.ResetMax();  // 清除前序操作可能设置的 Max 限制
```

### 4.5 测试串行化配置

#### 4.5.1 EverythingTestCollection.cs（新文件）

```csharp
// src/DirectoryCleanAgent.Tests/Infrastructure/EverythingTestCollection.cs
using DirectoryCleanAgent.Everything;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DirectoryCleanAgent.Tests.Infrastructure;

/// <summary>
/// Everything SDK 集成测试集合定义。
/// 所有使用 Everything64.dll 全局状态的测试类必须加入此集合，
/// 确保集合内测试串行执行，避免原生 SDK 全局状态竞争。
/// </summary>
[CollectionDefinition("Everything")]
public class EverythingTestCollection : ICollectionFixture<EverythingTestFixture>
{
    // 此类仅用于 xUnit CollectionDefinition 识别，不需要成员。
}

/// <summary>
/// Everything 集成测试集合夹具。
/// 生命周期（IAsyncLifetime）：
///   - InitializeAsync: 集合中第一个测试执行前调用
///   - DisposeAsync: 集合中最后一个测试执行后调用
/// </summary>
public sealed class EverythingTestFixture : IAsyncLifetime
{
    private ServiceProvider? _sdkProvider;

    /// <summary>Everything 环境是否可用</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>共享的 IEverythingSdk 实例（可选，供扩展方案使用）</summary>
    public IEverythingSdk? SharedSdk { get; private set; }

    public async Task InitializeAsync()
    {
        // 1. 重置静态缓存，确保以最新环境状态开始
        EverythingTestHelper.ResetCache();

        // 2. 缓存可用性检测结果
        IsAvailable = EverythingTestHelper.IsAvailable;

        // 3. 预建共享 SDK 实例
        if (IsAvailable)
        {
            try
            {
                var services = new ServiceCollection();
                services.AddLogging(b => { b.ClearProviders(); b.SetMinimumLevel(LogLevel.Warning); });
                services.AddEverythingServices();
                _sdkProvider = services.BuildServiceProvider();
                SharedSdk = _sdkProvider.GetRequiredService<IEverythingSdk>();
            }
            catch { SharedSdk = null; }
        }
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_sdkProvider is not null)
        {
            try { await _sdkProvider.DisposeAsync(); }
            catch { /* 释放异常不传播 */ }
        }
    }
}
```

#### 4.5.2 需要添加 [Collection] 的测试类

4 个文件（20 个 `[EverythingFact]` + 其他 `[Fact]` 测试），在类声明上方添加 `[Collection("Everything")]`：

| # | 文件 | 类名 | [EverythingFact] | [Fact] |
|---|------|------|:-:|:-:|
| 1 | `ScanCancellationTests.cs` | `ScanCancellationTests` | 1 | 1 |
| 2 | `EverythingScanIntegrationTests.cs` | `EverythingScanIntegrationTests` | 8 | 0 |
| 3 | `EverythingDetectionIntegrationTests.cs` | `EverythingDetectionIntegrationTests` | 8 | 1 |
| 4 | `IndexNotReadyIntegrationTests.cs` | `IndexNotReadyIntegrationTests` | 2 | 4 |

修改示例（每个文件在类声明上方添加一行）：

```csharp
[Collection("Everything")]           // ← 新增
public class ScanCancellationTests : IntegrationTestBase
{
    // ... 保持不变 ...
}
```

> **注意**: `IntegrationTestBase` 在构造函数中创建独立临时目录，每个测试实例互不干扰。因此不需要 `ICollectionFixture` 共享状态——仅串行化就足以解决全局 SDK 状态竞争问题。

### 4.6 已确认无需变更的项目

| 项目 | 结论 | 理由 |
|------|------|------|
| `IsIndexingAsync` 加锁 | 不需要 | `IsDBLoaded()` 是只读操作，不修改 SDK 全局状态；调用频率低且不依赖查询上下文 |
| `CheckVersion()` 加锁 | 不需要 | 纯读取 DLL 编译时常量，无副作用 |
| `CheckProcessAlive()` 改造 | 不需要 | 只检查操作系统进程列表，不访问 SDK |
| 现有测试适配 | 不需要 | `Mock<IEverythingSdk>` 自动处理新增的默认接口方法 |
| `EverythingTestFixture` | 已包含 | 集合夹具提供 Everything 环境缓存重置和共享 SDK 实例（可选扩展） |
| `_sdk` 参数改为非空 | 保留可空 | 向后兼容；`_sdk=null` 的后备原生路径提供防御性降级 |

---

## 五、实施顺序

| 步骤 | 文件 | 操作 | 预计改动量 |
|------|------|------|:--:|
| 1 | `EverythingNative.cs` | 添加 `Everything_GetMax()` P/Invoke | +5 行 |
| 2 | `IEverythingSdk.cs` | 添加 `GetMax()`、`SetMax()`、`ResetMax()` | +12 行 |
| 3 | `EverythingSdkWrapper.cs` | 实现 3 个新方法 | +20 行 |
| 4 | `EverythingSdkLock.cs` | **新建**共享锁类 | +40 行 |
| 5 | `ServiceRegistration.cs` | 注册 `EverythingSdkLock` 为 Singleton | +1 行 |
| 6 | `EverythingFileListProvider.cs` | 注入锁 + 添加 `ResetMax()` 调用 + 移除 Dispose 中的锁释放 | ~15 行 |
| 7 | `EverythingDependencyDetector.cs` | 注入锁 + 4 个方法改造 + 移除 Cleanup | ~120 行 |
| 8 | `App.xaml.cs` | 移除 `Cleanup()` 调用 | -2 行 |
| 9 | `EverythingTestCollection.cs` | **新建**测试串行化配置 | +15 行 |
| 10 | 4 个测试文件 | 每个添加 `[Collection("Everything")]` | +4 行 |
| 11 | 编译 + 运行测试 | `dotnet build` + `dotnet test --filter "FullyQualifiedName~Integration"` | — |
| 12 | 回归测试 | 完整测试套件 | — |

**总预计改动**: 约 230 行新增/修改，9 个文件变更，2 个新文件。
