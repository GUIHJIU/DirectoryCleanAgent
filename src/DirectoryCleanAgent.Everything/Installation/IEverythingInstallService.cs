namespace DirectoryCleanAgent.Everything.Installation;

/// <summary>
/// Everything 便携版安装服务接口。
/// </summary>
public interface IEverythingInstallService
{
    /// <summary>
    /// 下载并安装 Everything 便携版到应用目录下。
    /// </summary>
    /// <param name="appBaseDir">应用程序基础目录</param>
    /// <param name="progress">安装进度报告</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>安装后的 Everything.exe 完整路径</returns>
    Task<string> InstallPortableAsync(
        string appBaseDir,
        IProgress<InstallProgress> progress,
        CancellationToken ct);

    /// <summary>
    /// 卸载由本服务安装的 Everything 便携版。
    /// </summary>
    Task UninstallAsync(string appBaseDir);
}
