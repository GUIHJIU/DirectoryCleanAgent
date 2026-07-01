using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Everything.Installation;

/// <summary>
/// Everything 便携版安装服务。
/// 支持从 voidtools 下载或使用本地 Redist 备份，
/// 将 Everything portable 安装到应用目录下并启动服务。
/// </summary>
public class EverythingInstallService : IEverythingInstallService
{
    private readonly ILogger<EverythingInstallService> _logger;

    // Everything 便携版 x64 下载地址
    private const string PortableDownloadUrl =
        "https://www.voidtools.com/Everything-1.4.1.1026.x64.zip";

    // 应用目录下的 Everything 子目录名
    private const string EverythingSubDir = "Everything";

    // 离线 Redist 备用 ZIP 文件名
    private const string RedistZipName = "Everything-Portable.zip";

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public EverythingInstallService(ILogger<EverythingInstallService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>获取 Everything.exe 在应用目录下的预期路径</summary>
    public static string GetExpectedExePath(string appBaseDir)
    {
        return Path.Combine(appBaseDir, EverythingSubDir, "Everything.exe");
    }

    /// <inheritdoc />
    public async Task<string> InstallPortableAsync(
        string appBaseDir,
        IProgress<InstallProgress> progress,
        CancellationToken ct)
    {
        var extractDir = Path.Combine(appBaseDir, EverythingSubDir);
        string? zipPath = null;

        try
        {
            zipPath = await GetZipAsync(appBaseDir, progress, ct);

            // ---- 解压 ----
            progress.Report(new InstallProgress
            {
                Stage = InstallStage.Extracting,
                Percentage = 0,
                Message = "正在解压文件…"
            });

            Directory.CreateDirectory(extractDir);
            ct.ThrowIfCancellationRequested();

            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            _logger.LogInformation("Everything 解压完成到: {Dir}", extractDir);

            // ---- 验证 ----
            var exePath = GetExpectedExePath(appBaseDir);
            if (!File.Exists(exePath))
            {
                throw new EverythingInstallException(
                    InstallStage.Extracting,
                    $"解压后未找到 Everything.exe: {exePath}");
            }

            // ---- 注册客户端服务（非管理员）- 非致命 ----
            progress.Report(new InstallProgress
            {
                Stage = InstallStage.Installing,
                Percentage = 50,
                Message = "正在注册 Everything 用户服务…"
            });
            ct.ThrowIfCancellationRequested();

            try
            {
                await RunEverythingCommandAsync(exePath, "-install-client", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Everything -install-client 失败，便携版仍可直接运行");
            }

            // ---- 启动 Everything ----
            progress.Report(new InstallProgress
            {
                Stage = InstallStage.Verifying,
                Percentage = 80,
                Message = "正在启动 Everything…"
            });

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-startup",
                UseShellExecute = true,
            });

            await Task.Delay(2000, ct);

            progress.Report(new InstallProgress
            {
                Stage = InstallStage.Complete,
                Percentage = 100,
                Message = "Everything 安装完成"
            });

            _logger.LogInformation("Everything 便携版安装完成: {ExePath}", exePath);
            return exePath;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Everything 安装被取消");
            CleanupPartial(appBaseDir, zipPath);
            throw;
        }
        catch (EverythingInstallException)
        {
            CleanupPartial(appBaseDir, zipPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装 Everything 意外失败");
            CleanupPartial(appBaseDir, zipPath);
            throw new EverythingInstallException(
                InstallStage.Failed, $"安装失败: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task UninstallAsync(string appBaseDir)
    {
        var exePath = GetExpectedExePath(appBaseDir);

        if (File.Exists(exePath))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "-uninstall-client",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null) await proc.WaitForExitAsync();
            }
            catch { /* best effort */ }
        }

        // 终止由我们安装的 Everything 进程
        foreach (var proc in Process.GetProcessesByName("Everything"))
        {
            try
            {
                var mainModule = proc.MainModule;
                if (mainModule?.FileName?.StartsWith(appBaseDir,
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    proc.Kill();
                    proc.WaitForExit(5000);
                }
            }
            catch { /* best effort */ }
            finally { proc.Dispose(); }
        }

        var installDir = Path.Combine(appBaseDir, EverythingSubDir);
        if (Directory.Exists(installDir))
        {
            Directory.Delete(installDir, recursive: true);
        }

        _logger.LogInformation("Everything 便携版已卸载: {Dir}", installDir);
    }

    // ================================================================
    // Private helpers
    // ================================================================

    /// <summary>
    /// 获取 Everything ZIP：优先使用本地 Redist 备份，否则从 voidtools 下载。
    /// </summary>
    private async Task<string> GetZipAsync(
        string appBaseDir, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        // 策略 1: 本地 Redist 备份
        var redistPath = Path.Combine(appBaseDir, "Redist", RedistZipName);
        if (File.Exists(redistPath))
        {
            _logger.LogInformation("使用本地 Redist 备份: {Path}", redistPath);
            progress.Report(new InstallProgress
            {
                Stage = InstallStage.Downloading,
                Percentage = 100,
                Message = "使用本地安装包…",
                BytesDownloaded = new FileInfo(redistPath).Length,
                TotalBytes = new FileInfo(redistPath).Length,
            });
            return redistPath;
        }

        // 策略 2: 从 voidtools 下载
        _logger.LogInformation("从 voidtools 下载: {Url}", PortableDownloadUrl);

        var tempZip = Path.Combine(Path.GetTempPath(),
            $"EverythingPortable_{Guid.NewGuid():N}.zip");

        using var response = await _httpClient.GetAsync(
            PortableDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(tempZip, FileMode.Create,
            FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long bytesRead = 0;
        int len;

        while ((len = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, len, ct);
            bytesRead += len;

            if (totalBytes > 0)
            {
                var pct = (int)(bytesRead * 100 / totalBytes);
                progress.Report(new InstallProgress
                {
                    Stage = InstallStage.Downloading,
                    Percentage = pct,
                    Message = $"正在下载 Everything… {pct}%",
                    BytesDownloaded = bytesRead,
                    TotalBytes = totalBytes,
                });
            }
        }

        _logger.LogInformation("下载完成: {Path} ({Size} bytes)", tempZip, bytesRead);
        return tempZip;
    }

    private static async Task RunEverythingCommandAsync(
        string exePath, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi);
        if (proc != null)
        {
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                var err = await proc.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException(
                    $"Everything 命令失败 (exit={proc.ExitCode}): {err}");
            }
        }
    }

    private static void CleanupPartial(string appBaseDir, string? zipPath)
    {
        try { if (zipPath != null && File.Exists(zipPath)) File.Delete(zipPath); }
        catch { /* best effort */ }

        // 注：不删除部分解压的目录 — 可能包含调试信息
    }
}
