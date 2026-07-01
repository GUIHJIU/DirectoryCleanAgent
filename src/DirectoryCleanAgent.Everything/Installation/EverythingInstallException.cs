namespace DirectoryCleanAgent.Everything.Installation;

/// <summary>
/// Everything 安装过程中的异常。
/// </summary>
public class EverythingInstallException : Exception
{
    public InstallStage FailedStage { get; }

    public EverythingInstallException(InstallStage stage, string message, Exception? inner = null)
        : base(message, inner)
    {
        FailedStage = stage;
    }
}
