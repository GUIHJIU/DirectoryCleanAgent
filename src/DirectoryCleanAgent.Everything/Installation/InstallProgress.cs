namespace DirectoryCleanAgent.Everything.Installation;

/// <summary>
/// Everything 安装进度阶段。
/// </summary>
public enum InstallStage
{
    Downloading,
    Extracting,
    Installing,
    Verifying,
    Complete,
    Failed,
}

/// <summary>
/// Everything 安装进度报告 DTO。
/// </summary>
public class InstallProgress
{
    public InstallStage Stage { get; init; }
    public int Percentage { get; init; }
    public string Message { get; init; } = string.Empty;
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
}
