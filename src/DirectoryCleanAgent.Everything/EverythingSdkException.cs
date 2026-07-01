namespace DirectoryCleanAgent.Everything;

/// <summary>
/// Everything SDK 调用异常。
///
/// 封装 Everything SDK 调用失败时的错误上下文，包括：
/// - 失败的 SDK 函数名称
/// - Everything_GetLastError 返回的错误码
/// - 可选的附加描述信息
/// </summary>
public class EverythingSdkException : Exception
{
    /// <summary>引发异常的 Everything SDK 函数名称</summary>
    public string SdkFunctionName { get; }

    /// <summary>Everything_GetLastError() 返回的错误码（0=无错误/未知）</summary>
    public uint SdkErrorCode { get; }

    /// <summary>
    /// 构造 EverythingSdkException 实例。
    /// </summary>
    /// <param name="functionName">SDK 函数名称（如 "Everything_Query"）</param>
    /// <param name="errorCode">Everything_GetLastError() 返回值</param>
    /// <param name="message">附加错误描述</param>
    public EverythingSdkException(string functionName, uint errorCode, string message)
        : base($"[Everything SDK] {functionName} 失败 (错误码={errorCode}): {message}")
    {
        SdkFunctionName = functionName;
        SdkErrorCode = errorCode;
    }

    /// <summary>
    /// 构造带内部异常的 EverythingSdkException 实例。
    /// </summary>
    public EverythingSdkException(string functionName, uint errorCode, string message, Exception inner)
        : base($"[Everything SDK] {functionName} 失败 (错误码={errorCode}): {message}", inner)
    {
        SdkFunctionName = functionName;
        SdkErrorCode = errorCode;
    }
}
