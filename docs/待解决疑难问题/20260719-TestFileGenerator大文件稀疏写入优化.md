# TestFileGenerator 大文件稀疏写入导致磁盘空间耗尽

- **发现日期**: 2026-07-19（首次观测 2026-07-18）
- **状态**: 待处理
- **优先级**: 中（在磁盘空间有限的 CI/测试环境中阻塞集成测试）

## 现象

`ScanRuleEvaluateIntegrationTests.ScanEvaluateDecide_RealFiles_ThroughPipeline` 在调用 `TestFileGenerator.GenerateForRuleEngine(countPerCategory: 100)` 时抛出 `IOException`：

```
System.IO.IOException : 磁盘空间不足。
: 'C:\Users\...\AppData\Local\Temp\scan_eval_...\Users\TestUser\Downloads\test_000040.zip'
   at TestFileGenerator.WriteFile(String filePath, Int64 size)  (TestFileGenerator.cs:246)
```

## 根因分析

`TestFileGenerator.GenerateForRuleEngine(100)` 生成 400 个测试文件，其中"下载旧文件"类别（`Users\TestUser\Downloads`）的文件大小范围为 **1MB – 100MB**。

`WriteFile` 对大文件（> 10MB）采用"稀疏策略"（第 236-247 行）：

```csharp
// TestFileGenerator.cs:236-247
byte[] header = new byte[headerSize];          // 64KB
Random.Shared.NextBytes(header);
File.WriteAllBytes(filePath, header);           // 写入 64KB 头部

using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
byte[] footer = new byte[headerSize];           // 64KB
Random.Shared.NextBytes(footer);
fs.Seek(size - headerSize, SeekOrigin.Begin);   // 跳转到 (size-64KB) 位置
fs.Write(footer, 0, footer.Length);             // 写入 64KB 尾部
```

**缺陷**：`FileStream` 构造函数未传入 `FileOptions.SparseFile` 标志。在 Windows NTFS 上，`Seek` 越过文件尾后执行 `Write` 时，OS **必须为零填充整个空洞区域**分配物理磁盘空间。

对于 100MB 文件：128KB 有效数据 + ~100MB 零填充 = **实际写入 ~100MB**。400 个文件中约 75-100 个此类大文件，**总物理写入量可达数 GB**，在磁盘空间有限的测试机上直接耗尽剩余空间。

### 正确做法

```csharp
// 启用稀疏文件支持，避免零填充物理分配
using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None,
    bufferSize: 4096, FileOptions.SparseFile);
fs.SetLength(size);  // 预分配文件大小（稀疏空洞不占物理空间）
fs.Seek(size - headerSize, SeekOrigin.Begin);
fs.Write(footer, 0, footer.Length);
```

## 影响

- 在 C 盘（`%TEMP%`）空间不足时，`ScanEvaluateDecide_RealFiles_ThroughPipeline` 必然失败
- 即使空间充足，大文件零填充也显著拖慢测试执行速度（100MB × N 个文件的 IO 时间）
- 已在两个独立日期（2026-07-18、2026-07-19）复现

## 建议处理方向

1. **启用 `FileOptions.SparseFile`**：在 `WriteFile` 的大文件分支中传入该标志，并显式调用 `SetLength(size)` 预分配文件大小。NTFS 稀疏文件仅对写入的非零数据分配物理空间，128KB 即可模拟任意大小文件
2. **增加磁盘空间预检**：`GenerateForRuleEngine` 执行前检查目标卷剩余空间，不足时使用 `SkippableFact` 跳过并给出明确原因
3. **降低测试文件大小上限**：将"下载旧文件"类别的最大文件从 100MB 降至 10MB，减少极端情况

## 环境备注

2026-07-18/19 测试机 C 盘可用空间不足。`%TEMP%` 位于 C 盘，`TestFileGenerator` 生成的临时文件直接受此影响。
