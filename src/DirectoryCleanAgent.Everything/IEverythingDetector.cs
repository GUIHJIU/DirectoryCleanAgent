using DirectoryCleanAgent.Core.Config;

namespace DirectoryCleanAgent.Everything;

/// <summary>
/// Everything 依赖检测器接口 — 供 SetupWizardViewModel 等消费者解耦依赖。
/// </summary>
public interface IEverythingDetector
{
    /// <summary>执行 5 步检测序列</summary>
    Task<EverythingDetectionResult> DetectAsync(IConfigService configService, CancellationToken ct = default);

    /// <summary>等待索引就绪（轮询模式）</summary>
    Task<bool> WaitForIndexAsync(CancellationToken ct = default, IProgress<int>? progress = null);
}
