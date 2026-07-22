using System.Diagnostics;
using System.Text;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Everything.Interop;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Everything;

/// <summary>
/// Everything 依赖检测器 — 执行启动时的 5 步检测序列（适配 SDK 2.0 / Everything ≥ 1.4.1）。
///
/// 检测序列（按设计文档 V3.7 第 1.4 节）：
/// 1. 进程存活检查 — 验证 Everything.exe 进程正在运行。
/// 2. IPC 通信验证 — 通过一次轻量搜索验证 SDK ↔ Everything 进程通信正常。
/// 3. 版本检查 — Everything_GetMajorVersion() 等，要求 ≥ 1.4.1。
/// 4. FRN 能力探测 — 查询 C:\Windows\explorer.exe 的 FRN 字段，失败仅降级。
/// 5. 索引就绪检测 — 查询 IsDBLoaded 状态，未就绪提供等待/后台等待/跳过选项。
///
/// 前 3 步任一失败 → 禁止进入主界面并引导用户排查。
/// FRN 不可用 → 设置 UserConfig.FRN_AVAILABLE = false，墓碑降级为指纹模式。
/// 索引未就绪 → 轮询等待（2s 间隔，120s 超时）或跳过进入警告模式。
/// </summary>
public class EverythingDependencyDetector : IEverythingDetector
{
    // Everything 最低版本要求：1.4.1（稳定支持 FRN 的起始版本）
    private const int RequiredMajor = 1;
    private const int RequiredMinor = 4;
    private const int RequiredRevision = 1;

    // 索引就绪轮询间隔（毫秒）与超时（秒）
    private const int IndexPollIntervalMs = 2000;
    private const int IndexTimeoutSeconds = 120;

    // IPC 探测目标文件：使用已知必然存在的系统文件
    private const string IpcProbeFilePath = @"C:\Windows\explorer.exe";

    // 路径缓冲区大小（支持 \\?\ 长路径）
    private const int PathBufferSize = 4096;

    private readonly ILogger<EverythingDependencyDetector> _logger;
    private readonly IEverythingSdk? _sdk;
    private readonly EverythingSdkLock _sdkLock;

    public EverythingDependencyDetector(
        ILogger<EverythingDependencyDetector> logger,
        EverythingSdkLock sdkLock,
        IEverythingSdk? sdk = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sdkLock = sdkLock ?? throw new ArgumentNullException(nameof(sdkLock));
        _sdk = sdk;
    }

    // ================================================================
    // 主入口：执行完整检测序列
    // ================================================================

    /// <summary>
    /// 执行完整的 Everything 依赖检测序列（5 步，SDK 2.0）。
    ///
    /// 步骤 1-3 任一失败即返回 FailureReason 非空的失败结果；
    /// 步骤 4 失败仅标记 FRN_AVAILABLE = false，不阻断；
    /// 步骤 5 索引构建中标记 IsIndexing = true，不阻断。
    /// </summary>
    public async Task<EverythingDetectionResult> DetectAsync(
        IConfigService configService, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogMethodEntry("开始 Everything 依赖检测序列（5 步，SDK 2.0）");

        try
        {
            // ---- 步骤 1：进程存活检查 ----
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("[检测 1/5] 检查 Everything 进程是否运行...");
            bool processAlive = CheckProcessAlive();
            if (!processAlive)
            {
                var (installed, exePath) = CheckEverythingInstalled();

                if (!installed)
                {
                    const string msg = "未在系统上检测到 Everything 搜索引擎。";
                    _logger.LogError(msg);
                    return EverythingDetectionResult.CreateFailure(
                        msg, sw.ElapsedMilliseconds,
                        installStatus: EverythingInstallStatus.NotInstalled,
                        installSuggestion: "install");
                }

                const string runningMsg = "Everything 已安装但未运行。请启动 Everything 后再试。";
                _logger.LogError(runningMsg);
                return EverythingDetectionResult.CreateFailure(
                    runningMsg, sw.ElapsedMilliseconds,
                    installStatus: EverythingInstallStatus.Installed,
                    installSuggestion: "start",
                    exePath: exePath);
            }
            _logger.LogInformation("[检测 1/5] Everything 进程存活 — 通过");

            // ---- SDK 版本探测（步骤 1 之后）：尝试 SDK 3.0，失败则回退 SDK 2.0 ----
            int sdkVersion = 2;
            IntPtr clientHandle = IntPtr.Zero;
            try
            {
                clientHandle = Everything3Native.Everything3_ConnectW(null);
                if (clientHandle != IntPtr.Zero)
                {
                    sdkVersion = 3;
                    _logger.LogInformation("检测到 Everything SDK 3.0（命名管道模式），客户端句柄: 0x{Handle:X}",
                        clientHandle);
                }
            }
            catch (EntryPointNotFoundException)
            {
                _logger.LogInformation("Everything64.dll 不含 SDK 3.0 导出，回退 SDK 2.0（全局状态模式）");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SDK 3.0 探测失败，回退 SDK 2.0");
            }

            // ---- 步骤 2：IPC 通信验证（SDK 2.0 通过执行一次搜索来验证）----
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("[检测 2/5] 验证 Everything IPC 通信...");
            bool ipcOk = await PingIpcAsync(ct);
            if (!ipcOk)
            {
                const string msg = "Everything IPC 通信失败。" +
                                   "请确认 Everything 已启动且索引服务正常运行。";
                _logger.LogError(msg);
                return new EverythingDetectionResult
                {
                    ProcessAlive = true,
                    FailureReason = msg,
                    ElapsedMilliseconds = sw.ElapsedMilliseconds,
                    InstallStatus = EverythingInstallStatus.Installed,
                    InstallSuggestion = "check-running",
                };
            }
            _logger.LogInformation("[检测 2/5] Everything IPC 通信 — 通过");

            // ---- 步骤 3：版本检查 ----
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("[检测 3/5] 检查 Everything 版本...");
            var (versionOk, major, minor, rev, build) = CheckVersion();
            if (!versionOk)
            {
                var msg = $"Everything 版本过低：{major}.{minor}.{rev}.{build}。" +
                          $"需要 {RequiredMajor}.{RequiredMinor}.{RequiredRevision}.x 或更高版本。";
                _logger.LogWarning(msg);
                return new EverythingDetectionResult
                {
                    ProcessAlive = true,
                    MajorVersion = major,
                    MinorVersion = minor,
                    Revision = rev,
                    BuildNumber = build,
                    VersionOk = false,
                    IpcPingOk = true,
                    FailureReason = msg,
                    ElapsedMilliseconds = sw.ElapsedMilliseconds,
                    InstallStatus = EverythingInstallStatus.Installed,
                    InstallSuggestion = "upgrade",
                };
            }
            _logger.LogInformation(
                "[检测 3/5] Everything 版本 {Major}.{Minor}.{Rev}.{Build} — 通过",
                major, minor, rev, build);

            // ---- 步骤 4：FRN 能力探测 ----
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("[检测 4/5] 探测 FRN（File Reference Number）能力...");
            bool frnAvailable = await DetectFrnCapabilityAsync(ct);
            if (!frnAvailable)
            {
                _logger.LogWarning(
                    "[检测 4/5] FRN 不可用 — 墓碑匹配将降级为 Size+LastWriteTime 指纹模式（3 天强制过期）");
                configService.Current.FRN_AVAILABLE = false;
                await configService.SaveAsync(ct);
            }
            else
            {
                _logger.LogInformation("[检测 4/5] FRN 能力 — 通过");
                configService.Current.FRN_AVAILABLE = true;
                await configService.SaveAsync(ct);
            }

            // ---- 步骤 5：索引就绪检测 ----
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("[检测 5/5] 检查 Everything 索引就绪状态...");
            bool isIndexing = !await CheckIndexReadyAsync(ct);
            if (isIndexing)
            {
                _logger.LogWarning("[检测 5/5] Everything 正在构建索引 — 当前结果可能不完整");
            }
            else
            {
                _logger.LogInformation("[检测 5/5] Everything 索引就绪 — 通过");
            }

            sw.Stop();
            _logger.LogInformation(
                "Everything 依赖检测完成 — 总耗时 {Elapsed}ms, 可进入主界面: {CanProceed}",
                sw.ElapsedMilliseconds, true);

            return new EverythingDetectionResult
            {
                ProcessAlive = true,
                MajorVersion = major,
                MinorVersion = minor,
                Revision = rev,
                BuildNumber = build,
                VersionOk = true,
                IpcPingOk = true,
                FRN_AVAILABLE = frnAvailable,
                DBLoaded = EverythingNative.Everything_IsDBLoaded(),
                IsIndexing = isIndexing,
                RawVersion = (uint)((major << 24) | (minor << 16) | (rev << 8) | build),
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                SdkVersion = sdkVersion,
                ClientHandle = clientHandle,
            };
        }
        catch (DllNotFoundException ex)
        {
            sw.Stop();
            const string msg = "未找到 Everything64.dll。请确保 Everything 已正确安装。";
            _logger.LogError(ex, msg);
            return EverythingDetectionResult.CreateFailure(msg, sw.ElapsedMilliseconds);
        }
        catch (EntryPointNotFoundException ex)
        {
            sw.Stop();
            var msg = $"Everything64.dll 接口不兼容：{ex.Message}。";
            _logger.LogError(ex, msg);
            return EverythingDetectionResult.CreateFailure(msg, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning("Everything 依赖检测被用户取消");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            var msg = $"Everything 依赖检测期间发生意外错误：{ex.Message}";
            _logger.LogError(ex, msg);
            return EverythingDetectionResult.CreateFailure(msg, sw.ElapsedMilliseconds);
        }
    }

    // ================================================================
    // 步骤 1：进程存活检查
    // ================================================================

    /// <summary>
    /// 检查 Everything 进程是否正在运行，同时记录进程路径供 DLL 加载。
    /// </summary>
    internal static bool CheckProcessAlive()
    {
        try
        {
            var processes = Process.GetProcessesByName("Everything");
            if (processes.Length > 0)
            {
                try
                {
                    var mainModule = processes[0].MainModule;
                    if (mainModule != null)
                    {
                        EverythingNative.EverythingProcessDirectory =
                            Path.GetDirectoryName(mainModule.FileName);
                    }
                }
                catch
                {
                    // 权限不足时无法获取进程路径，不影响检测结果
                }

                try
                {
                    foreach (var p in processes) p.Dispose();
                }
                catch { /* ignore */ }

                return true;
            }
            return false;
        }
        catch (Exception)
        {
            // 权限不足或其它原因无法枚举进程时，后续 IPC 通信会进一步验证
            return true;
        }
    }

    /// <summary>
    /// 检测 Everything.exe 是否在系统上已安装（注册表/默认路径/PATH/捆绑目录）。
    /// </summary>
    internal static (bool found, string? exePath) CheckEverythingInstalled()
    {
        // 1. 从注册表查找
        string?[] registryPaths =
        [
            @"SOFTWARE\voidtools\Everything",
        ];

        foreach (var regPath in registryPaths)
        {
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine,
                                         Microsoft.Win32.RegistryHive.CurrentUser })
            {
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                        hive, Microsoft.Win32.RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(regPath);
                    var installDir = key?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(installDir))
                    {
                        var exePath = Path.Combine(installDir, "Everything.exe");
                        if (File.Exists(exePath))
                            return (true, exePath);
                    }
                }
                catch { /* skip inaccessible hive */ }
            }
        }

        // 2. 检查默认安装路径
        string?[] defaultPaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Everything", "Everything.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Everything", "Everything.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Everything", "Everything.exe"),
        ];

        foreach (var path in defaultPaths)
        {
            if (File.Exists(path))
                return (true, path);
        }

        // 3. 检查 PATH 环境变量
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var exePath = Path.Combine(dir.Trim(), "Everything.exe");
            if (File.Exists(exePath))
                return (true, exePath);
        }

        // 4. 检查应用捆绑的便携版目录
        var portableExe = Path.Combine(AppContext.BaseDirectory, "Everything", "Everything.exe");
        if (File.Exists(portableExe))
            return (true, portableExe);

        return (false, null);
    }

    // ================================================================
    // 步骤 2：IPC 通信验证（SDK 2.0）
    // ================================================================

    /// <summary>
    /// 通过执行一次轻量搜索来验证 Everything IPC 通信是否正常。
    /// SDK 2.0 无显式连接步骤，直接通过搜索验证通信。
    /// 在 _sdkLock 保护下执行。
    /// </summary>
    private async Task<bool> PingIpcAsync(CancellationToken ct)
    {
        await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
        uint prevMax = 0;
        try
        {
            if (_sdk != null)
            {
                prevMax = _sdk.GetMax();
                _sdk.SetMax(1);
                _sdk.SetSearch(IpcProbeFilePath);

                if (!_sdk.Query(true))
                    return false;

                uint error = _sdk.GetLastError();
                uint count = _sdk.GetNumResults();

                _sdk.SetMax(prevMax);
                return error == EverythingNative.ERROR_OK && count > 0;
            }
            else
            {
                prevMax = EverythingNative.Everything_GetMax();
                EverythingNative.Everything_SetMax(1);
                EverythingNative.Everything_SetSearch(IpcProbeFilePath);

                if (!EverythingNative.Everything_Query(true))
                    return false;

                uint error = EverythingNative.Everything_GetLastError();
                uint count = EverythingNative.Everything_GetNumResults();

                EverythingNative.Everything_SetMax(prevMax);
                return error == EverythingNative.ERROR_OK && count > 0;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _sdkLock.Release();
        }
    }

    // ================================================================
    // 步骤 3：版本检查
    // ================================================================

    /// <summary>
    /// 检查 Everything 版本是否满足最低要求（≥ 1.4.1）。
    /// SDK 2.0 使用独立的版本字段函数。
    /// 只读操作，无需获取锁。
    /// </summary>
    private (bool ok, int major, int minor, int rev, int build) CheckVersion()
    {
        int major, minor, revision, build;
        if (_sdk != null)
        {
            major = (int)_sdk.GetMajorVersion();
            minor = (int)_sdk.GetMinorVersion();
            revision = (int)_sdk.GetRevision();
            build = (int)_sdk.GetBuildNumber();
        }
        else
        {
            major = (int)EverythingNative.Everything_GetMajorVersion();
            minor = (int)EverythingNative.Everything_GetMinorVersion();
            revision = (int)EverythingNative.Everything_GetRevision();
            build = (int)EverythingNative.Everything_GetBuildNumber();
        }

        bool ok = major > RequiredMajor ||
                  (major == RequiredMajor && minor > RequiredMinor) ||
                  (major == RequiredMajor && minor == RequiredMinor && revision >= RequiredRevision);

        return (ok, major, minor, revision, build);
    }

    // ================================================================
    // 步骤 4：FRN 能力探测
    // ================================================================

    /// <summary>
    /// 探测 Everything 是否支持返回文件引用号（File Reference Number）。
    /// 在 _sdkLock 保护下执行。
    /// </summary>
    private async Task<bool> DetectFrnCapabilityAsync(CancellationToken ct)
    {
        await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
        uint prevMax = 0;
        try
        {
            if (_sdk != null)
            {
                prevMax = _sdk.GetMax();
                _sdk.SetMax(1);
                _sdk.SetSearch(IpcProbeFilePath);

                if (!_sdk.Query(true) || _sdk.GetNumResults() == 0)
                {
                    // 无过滤搜索任意文件
                    _sdk.SetSearch(string.Empty);
                    _sdk.SetMax(1);

                    if (!_sdk.Query(true) || _sdk.GetNumResults() == 0)
                    {
                        _sdk.SetMax(prevMax);
                        return false;
                    }
                }

                long frn = _sdk.GetResultFileReferenceNumber(0);
                uint error = _sdk.GetLastError();

                _sdk.SetMax(prevMax);
                return frn > 0 && error == EverythingNative.ERROR_OK;
            }
            else
            {
                prevMax = EverythingNative.Everything_GetMax();
                EverythingNative.Everything_SetMax(1);
                EverythingNative.Everything_SetSearch(IpcProbeFilePath);

                if (!EverythingNative.Everything_Query(true) ||
                    EverythingNative.Everything_GetNumResults() == 0)
                {
                    // 无过滤搜索任意文件
                    EverythingNative.Everything_SetSearch(string.Empty);
                    EverythingNative.Everything_SetMax(1);

                    if (!EverythingNative.Everything_Query(true) ||
                        EverythingNative.Everything_GetNumResults() == 0)
                    {
                        EverythingNative.Everything_SetMax(prevMax);
                        return false;
                    }
                }

                long nativeFrn = 0;
                EverythingNative.Everything_GetResultFileReferenceNumber(0, out nativeFrn);
                uint error = EverythingNative.Everything_GetLastError();

                EverythingNative.Everything_SetMax(prevMax);
                return nativeFrn > 0 && error == EverythingNative.ERROR_OK;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _sdkLock.Release();
        }
    }

    // ================================================================
    // 步骤 5：索引就绪检测
    // ================================================================

    /// <summary>
    /// 检查 Everything 索引是否就绪。
    /// 要求 DB 已加载且能成功执行一次查询。
    /// 在 _sdkLock 保护下执行。
    /// </summary>
    private async Task<bool> CheckIndexReadyAsync(CancellationToken ct)
    {
        await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
        uint prevMax = 0;
        try
        {
            // IsDBLoaded 是只读操作，优先检查
            if (_sdk != null)
            {
                if (!_sdk.IsDBLoaded())
                    return false;

                prevMax = _sdk.GetMax();
                _sdk.SetMax(1);
                _sdk.SetSearch(IpcProbeFilePath);

                bool ok = _sdk.Query(true) && _sdk.GetNumResults() > 0;

                _sdk.SetMax(prevMax);
                return ok;
            }
            else
            {
                if (!EverythingNative.Everything_IsDBLoaded())
                    return false;

                prevMax = EverythingNative.Everything_GetMax();
                EverythingNative.Everything_SetMax(1);
                EverythingNative.Everything_SetSearch(IpcProbeFilePath);

                bool ok = EverythingNative.Everything_Query(true)
                       && EverythingNative.Everything_GetNumResults() > 0;

                EverythingNative.Everything_SetMax(prevMax);
                return ok;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _sdkLock.Release();
        }
    }

    // ================================================================
    // 索引等待（120s 超时机制）
    // ================================================================

    /// <summary>
    /// 等待 Everything 索引构建完成。
    /// 优先使用注入的 IEverythingSdk，否则直接使用 SDK 2.0 原生 API。
    /// </summary>
    public async Task<bool> WaitForIndexAsync(
        CancellationToken ct = default,
        IProgress<int>? progress = null)
    {
        _logger.LogMethodEntry(
            $"开始等待 Everything 索引就绪（轮询间隔 {IndexPollIntervalMs}ms, 超时 {IndexTimeoutSeconds}s）");

        int elapsedSeconds = 0;

        bool useSdk = _sdk is { IsConnected: true };

        while (elapsedSeconds < IndexTimeoutSeconds)
        {
            ct.ThrowIfCancellationRequested();

            bool indexReady = useSdk
                ? _sdk!.IsDBLoaded()
                : await CheckIndexReadyAsync(ct);

            if (indexReady)
            {
                _logger.LogInformation(
                    "Everything 索引已就绪 — 等待耗时 {Elapsed}s", elapsedSeconds);
                progress?.Report(elapsedSeconds);
                return true;
            }

            progress?.Report(elapsedSeconds);
            await Task.Delay(IndexPollIntervalMs, ct);
            elapsedSeconds += IndexPollIntervalMs / 1000;
        }

        _logger.LogWarning(
            "Everything 索引等待超时（{Timeout}s），进入警告模式", IndexTimeoutSeconds);
        progress?.Report(elapsedSeconds);
        return false;
    }

}
