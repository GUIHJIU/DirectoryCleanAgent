# AccessViolationException 排查与修复

**日期**: 2026-06-26
**影响组件**: `DirectoryCleanAgent.Everything` — `EverythingNative.cs`, `EverythingSdkWrapper.cs`, `EverythingFileListProvider.cs`, `EverythingDependencyDetector.cs`
**严重级别**: 高（触发即崩溃，无任何托管层捕获能力）
**复现条件**: Everything 返回大量结果（>300 万条），触发刷新扫描或显示全部文件

---

## 故障现象

```
System.AccessViolationException
  Attempted to read or write protected memory. This is often an indication that other memory is corrupt.
  异常来源: DirectoryCleanAgent.Everything.dll
  函数: EverythingFileListProvider.EnumerateFilesAsync
```

触发时的运行时日志：

```
[17:32:17 INF] 用户触发刷新扫描
[17:32:17 INF] EnumerateFilesAsync: Search='(全部)', Sort=默认, ...
[17:32:23 INF] Everything 查询完成: 共 3132286 条结果
→ System.AccessViolationException 立即抛出（未进入 EnumerateFilesAsync 完成日志）
```

调用堆栈定位到 `EverythingFileListProvider.cs` 第 304 行：`_sdk.GetResultSize(i)`。

---

## 根因分析（三轮排查，三个根因）

### 根因一：变更轮询定时器竞态（锁覆盖不足）

**Everything SDK 2.0 的全局状态模型**：所有搜索结果存储在 SDK 的全局内存中。`Everything_Query()` 会覆盖上一个结果集。若在遍历期间释放锁，变更轮询定时器可能执行新查询并覆盖结果集。

**触发链路**：

```
[ThreadPool 定时器]  OnChangePollingTimerElapsed
  → _sdkLock.Wait(0)          ← 非阻塞尝试，锁已释放 → 成功获取
  → PollForFileChanges()
    → _sdk.SetSearch(timeQuery)  ← 覆盖全局搜索表达式
    → _sdk.Query(true)           ← 覆盖全局结果集
    → _sdkLock.Release()

[主枚举线程]          仍在 for 循环中
  → _sdk.GetResultFullPathName(i)  ← i=50，但新结果集只有 30 条
  → SDK 访问索引越界 → AccessViolationException
```

**修复**：将整个 `try { 查询 + 遍历 } finally { 释放锁 }` 放在锁内，`yield return` 在 `try` 块内，锁在枚举器 `DisposeAsync` 时才释放。同时变更轮询使用 `_sdkLock.Wait(0)` 零超时，枚举期间自动跳过。

---

### 根因二：P/Invoke 签名与原生函数不匹配（**核心根因**）

Everything SDK（≥ 1.4.1）的原生函数签名均采用 `BOOL` 返回值 + **指针输出参数**模式：

```cpp
BOOL Everything_GetResultSize(DWORD index, LARGE_INTEGER *lpSize);
BOOL Everything_GetResultDateModified(DWORD index, LARGE_INTEGER *lpDateModified);
BOOL Everything_GetResultFileReferenceNumber(DWORD index, LARGE_INTEGER *lpFRN);
BOOL Everything_GetResultVolumeSerial(DWORD index, DWORD *lpVolumeSerial);
```

但代码将这四个函数错误声明为**直接返回值、单参数**：

```csharp
// 错误 —— 原生函数需要 2 个参数，只传了 1 个
[DllImport(DllName)]
internal static extern long Everything_GetResultSize(uint index);                        // ← 缺少 out long lpSize

[DllImport(DllName)]
internal static extern long Everything_GetResultDateModified(uint index);                // ← 缺少 out long lpDateModified

[DllImport(DllName)]
internal static extern long Everything_GetResultFileReferenceNumber(uint index);         // ← 缺少 out long lpFRN

[DllImport(DllName)]
internal static extern uint Everything_GetResultVolumeSerial(uint index);                // ← 缺少 out uint lpVolumeSerial
```

**崩溃机制（x64 调用约定）**：

| 参数位置 | 寄存器 | 实际传入值 | 原生函数期望 |
|---------|--------|-----------|-------------|
| 第 1 参数 | RCX | `index`（正确） | `DWORD index` |
| 第 2 参数 | RDX | **垃圾值**（残留） | `LARGE_INTEGER *lpSize` |

原生函数收到 RDX 中的垃圾值作为 `lpSize` 指针，向该随机地址写入 8 字节（`LARGE_INTEGER`）→ 若地址不可写则立即 `AccessViolationException`；若地址"碰巧"可写则**静默损坏堆内存**，稍后在其他操作中崩溃。

这就是错误消息中 "other memory is corrupt" 的来源——堆已被随机写操作破坏。

**修复**：纠正所有四个 P/Invoke 声明为正确的两参数签名：

```csharp
[DllImport(DllName)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static extern bool Everything_GetResultSize(uint index, out long lpSize);

[DllImport(DllName)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static extern bool Everything_GetResultDateModified(uint index, out long lpDateModified);

[DllImport(DllName)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static extern bool Everything_GetResultFileReferenceNumber(uint index, out long lpFileReferenceNumber);

[DllImport(DllName)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static extern bool Everything_GetResultVolumeSerial(uint index, out uint lpVolumeSerial);
```

同时更新 `EverythingSdkWrapper.cs` 中四个包装方法的内部实现（公共 API 签名不变，仍返回 `long`/`uint`），以及 `EverythingDependencyDetector.cs` 中 `DetectFrnCapability()` 的直接调用。

---

### 根因三：REQUEST_FILE_REFERENCE_NUMBER 和 REQUEST_VOLUME_SERIAL 标志位无条件设置

`REQUEST_ALL` 中无条件包含 `REQUEST_FILE_REFERENCE_NUMBER`（0x00010000）和 `REQUEST_VOLUME_SERIAL`（0x00040000）。如果用户的 Everything SDK DLL 版本不完全支持这些标志位，设置未识别的标志位+调用对应的 getter 函数可能导致 SDK 内部状态异常。

此外，`GetResultFileReferenceNumber` 和 `GetResultVolumeSerial` 在 `frnAvailable == false` 时仍被调用（只是返回值未被使用），徒增不必要的原生调用。

**修复**：
1. 请求标志位以 `frnAvailable` 为条件动态构建
2. `GetResultFileReferenceNumber` 和 `GetResultVolumeSerial` 仅在 `frnAvailable == true` 时调用

---

## 变更文件汇总

| 文件 | 变更类型 | 说明 |
|------|----------|------|
| `EverythingNative.cs:154-219` | 修改 | 四个 P/Invoke 声明：改为 `bool` 返回值 + `out` 指针参数 |
| `EverythingSdkWrapper.cs:116-165` | 修改 | 四个包装方法：使用 `out` 参数调用原生函数 |
| `EverythingDependencyDetector.cs:421-422` | 修改 | `DetectFrnCapability()`：适配新的 FRN P/Invoke 签名 |
| `EverythingFileListProvider.cs:188-320` | 修改 | ① 锁覆盖 yield return 全周期 ② FRN/VolumeSerial 标志位与调用条件化 |

---

## 验证方法

1. **正常场景** — 启动应用，执行全盘扫描（期望 >100 万文件），确认不再崩溃
2. **并发场景** — 扫描期间等待至少 6 秒（2 个轮询周期），确认变更轮询正常跳过且扫描不受影响
3. **取消场景** — 扫描中途取消，确认锁正常释放，后续扫描可正常执行
4. **小结果集** — < 1000 条结果，功能无退化
5. **FRN 不可用环境** — 在非 NTFS 卷或旧版 Everything SDK 下，确认降级为指纹模式正常工作
