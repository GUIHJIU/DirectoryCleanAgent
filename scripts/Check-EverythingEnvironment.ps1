# ============================================================
# Check-EverythingEnvironment.ps1
# 功能: 检测/安装/验证 Everything SDK 环境
# 依赖: 无（独立运行）
# 对应任务: D1.5 — Everything 1.4.1.1000+ 安装与环境验证
# ============================================================

param(
    [string]$LogPath = "$env:TEMP\EverythingSetup_$(Get-Date -Format 'yyyyMMdd_HHmmss').log",
    [switch]$SkipDownload = $false,
    [int]$IndexingTimeoutSeconds = 120,
    [int]$PollIntervalSeconds = 2
)

#region ---- 初始化日志系统 ----
$script:LogStream = $null
$script:StartTime = Get-Date

function Initialize-Logger {
    param([string]$Path)
    try {
        $logDir = Split-Path $Path -Parent
        if (-not (Test-Path $logDir)) {
            New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        }
        $script:LogStream = [System.IO.StreamWriter]::new($Path, $true, [System.Text.Encoding]::UTF8)
        Write-Log "============================================================"
        Write-Log "Everything 环境检测脚本启动"
        Write-Log "日志文件: $Path"
        Write-Log "启动时间: $($script:StartTime.ToString('yyyy-MM-dd HH:mm:ss'))"
        Write-Log "============================================================"
    }
    catch {
        Write-Warning "无法创建日志文件: $Path — 仅输出到控制台"
        Write-Warning "错误详情: $($_.Exception.Message)"
    }
}

function Write-Log {
    param(
        [ValidateSet('INFO', 'WARN', 'ERROR', 'PASS', 'FAIL')]
        [string]$Level = 'INFO',
        [string]$Message
    )
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
    $line = "[$timestamp] [$Level] $Message"

    # 控制台输出（带颜色）
    switch ($Level) {
        'ERROR' { Write-Host $line -ForegroundColor Red }
        'FAIL'  { Write-Host $line -ForegroundColor Red }
        'WARN'  { Write-Host $line -ForegroundColor Yellow }
        'PASS'  { Write-Host $line -ForegroundColor Green }
        default { Write-Host $line }
    }

    # 文件输出
    if ($script:LogStream) {
        try { $script:LogStream.WriteLine($line); $script:LogStream.Flush() }
        catch { }
    }
}

function Close-Logger {
    if ($script:LogStream) {
        $elapsed = (Get-Date) - $script:StartTime
        Write-Log "============================================================"
        Write-Log "脚本执行完成，总耗时: $($elapsed.TotalSeconds.ToString('F1')) 秒"
        Write-Log "============================================================"
        try { $script:LogStream.Close(); $script:LogStream.Dispose() }
        catch { }
    }
}
#endregion

#region ---- Everything 检测核心逻辑 ----

# Everything 最低版本要求（设计文档 V3.7 强制要求）
$script:MinEverythingVersion = [System.Version]::new(1, 4, 1, 1000)

# 全局状态记录
$script:EverythingInstalled = $false
$script:EverythingVersion = $null
$script:IpcAvailable = $false
$script:IndexReady = $false
$script:FrnAvailable = $false

function Test-AdminPrivilege {
    # 检测当前是否以管理员权限运行
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Find-EverythingInstallPath {
    # 多路径探测 Everything 安装位置
    # 检测顺序: 注册表 → 默认路径 → PATH 环境变量
    try {
        # 策略1: 注册表查询（64位和32位注册表视图都要查）
        $registryPaths = @(
            'HKLM:\SOFTWARE\voidtools\Everything',
            'HKLM:\SOFTWARE\WOW6432Node\voidtools\Everything',
            'HKCU:\SOFTWARE\voidtools\Everything',
            'HKCU:\SOFTWARE\WOW6432Node\voidtools\Everything'
        )
        foreach ($regPath in $registryPaths) {
            try {
                $regValue = Get-ItemProperty -Path $regPath -Name 'InstallLocation' -ErrorAction Stop
                if ($regValue.InstallLocation) {
                    $exePath = Join-Path $regValue.InstallLocation 'Everything.exe'
                    if (Test-Path $exePath) {
                        Write-Log -Level INFO "从注册表找到 Everything: $exePath"
                        return $exePath
                    }
                }
            }
            catch { }
        }

        # 策略2: 默认安装路径
        $defaultPaths = @(
            "$env:ProgramFiles\Everything\Everything.exe",
            "${env:ProgramFiles(x86)}\Everything\Everything.exe",
            "$env:LOCALAPPDATA\Everything\Everything.exe"
        )
        foreach ($path in $defaultPaths) {
            if (Test-Path $path) {
                Write-Log -Level INFO "从默认路径找到 Everything: $path"
                return $path
            }
        }

        # 策略3: PATH 环境变量搜索
        $pathResult = Get-Command 'Everything.exe' -ErrorAction SilentlyContinue
        if ($pathResult) {
            Write-Log -Level INFO "从 PATH 中找到 Everything: $($pathResult.Source)"
            return $pathResult.Source
        }

        Write-Log -Level WARN "未找到 Everything 安装路径"
        return $null
    }
    catch {
        Write-Log -Level ERROR "查找 Everything 安装路径时异常: $($_.Exception.Message)"
        return $null
    }
}

function Get-EverythingVersion {
    param([string]$ExePath)
    # 通过 Everything.exe -version 获取版本号
    # Everything 返回格式: "Everything 1.4.1.1024 (x64)"
    try {
        $output = & $ExePath -version 2>&1
        $versionString = "$output"

        Write-Log -Level INFO "Everything -version 原始输出: $versionString"

        # 正则提取版本号: 主版本.次版本.修订版.构建号
        $versionMatch = [regex]::Match($versionString, '(\d+)\.(\d+)\.(\d+)\.(\d+)')
        if ($versionMatch.Success) {
            return [System.Version]::new(
                [int]$versionMatch.Groups[1].Value,
                [int]$versionMatch.Groups[2].Value,
                [int]$versionMatch.Groups[3].Value,
                [int]$versionMatch.Groups[4].Value
            )
        }
        return $null
    }
    catch {
        Write-Log -Level ERROR "获取 Everything 版本异常: $($_.Exception.Message)"
        return $null
    }
}

function Start-EverythingService {
    # 确保 Everything 服务和进程在运行
    try {
        # 检查 Everything 服务
        $service = Get-Service -Name 'Everything' -ErrorAction SilentlyContinue
        if ($service) {
            if ($service.Status -ne 'Running') {
                Write-Log -Level INFO "正在启动 Everything 服务..."
                Start-Service -Name 'Everything' -ErrorAction Stop
                Start-Sleep -Seconds 3 # 等待服务完全启动
                Write-Log -Level PASS "Everything 服务已启动"
            }
            else {
                Write-Log -Level INFO "Everything 服务已在运行中"
            }

            # 验证 Everything.exe 进程是否在运行
            $process = Get-Process -Name 'Everything' -ErrorAction SilentlyContinue
            if (-not $process) {
                Write-Log -Level WARN "Everything 服务运行中但进程未检测到，尝试启动..."
                Start-Process -FilePath (Find-EverythingInstallPath) -WindowStyle Hidden
                Start-Sleep -Seconds 2
            }
        }
        else {
            Write-Log -Level WARN "未找到 Everything 系统服务，尝试直接启动进程..."
            $exePath = Find-EverythingInstallPath
            if ($exePath) {
                Start-Process -FilePath $exePath -ArgumentList '-startup' -WindowStyle Hidden
                Start-Sleep -Seconds 3
                Write-Log -Level INFO "Everything 进程已启动（-startup 模式）"
            }
        }
    }
    catch {
        Write-Log -Level ERROR "启动 Everything 服务/进程时异常: $($_.Exception.Message)"
    }
}

function Test-EverythingIpc {
    # IPC 探活检测：验证 Everything 进程间通信是否正常
    # 方法: 使用 Everything SDK 的 Everything_GetVersion C# P/Invoke
    try {
        # 动态编译 C# 代码进行 IPC 探活
        $csharpCode = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class EverythingIpcProbe
{
    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetMajorVersion();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern void Everything_SetSearch(string search);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern bool Everything_Query(bool wait);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetNumResults();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetLastError();

    // IPC 探活：发送搜索请求并检查返回结果
    public static bool ProbeIpc()
    {
        try
        {
            uint version = Everything_GetMajorVersion();
            if (version == 0)
            {
                Console.Error.WriteLine("Everything_GetMajorVersion 返回 0，IPC 可能不可用");
                return false;
            }

            // 发起简单查询验证 IPC 通道
            Everything_SetSearch("C:\\Windows\\explorer.exe");
            if (!Everything_Query(true))
            {
                uint error = Everything_GetLastError();
                Console.Error.WriteLine($"Everything_Query 失败，错误码: {error}");
                return false;
            }

            uint results = Everything_GetNumResults();
            Console.WriteLine($"IPC 探活成功: 找到 {results} 条结果");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"IPC 探活异常: {ex.Message}");
            return false;
        }
    }
}
'@
        Add-Type -TypeDefinition $csharpCode -ReferencedAssemblies 'System' -ErrorAction Stop
        $result = [EverythingIpcProbe]::ProbeIpc()
        return $result
    }
    catch [System.Management.Automication.MethodInvocationException] {
        Write-Log -Level ERROR "IPC 探活编译/执行失败（DllNotFoundException: Everything64.dll 不在 PATH 中）"
        Write-Log -Level ERROR "请确保 Everything64.dll 与 Everything.exe 位于同一目录"
        return $false
    }
    catch {
        Write-Log -Level ERROR "IPC 探活异常: $($_.Exception.Message)"
        return $false
    }
}

function Test-EverythingFrnCapability {
    # FRN (File Reference Number) 能力检测
    # 查询 C:\Windows\explorer.exe 的 FRN 字段，验证 NTFS 物理 ID 能力
    try {
        $csharpCode = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class EverythingFrnProbe
{
    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern void Everything_SetSearch(string search);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern bool Everything_Query(bool wait);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetNumResults();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern void Everything_GetResultFullPathName(uint index, StringBuilder buf, uint bufSize);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern long Everything_GetResultFileReferenceNumber(uint index);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetLastError();

    // 返回 FRN 值（0 表示不可用），out 参数返回检查的文件路径
    public static long ProbeFrn(out string checkedFile)
    {
        checkedFile = "";
        try
        {
            // 查询已知系统文件
            Everything_SetSearch("C:\\Windows\\explorer.exe");
            if (!Everything_Query(true)) return 0;

            uint count = Everything_GetNumResults();
            if (count == 0)
            {
                // 回退：搜索 notepad.exe
                Everything_SetSearch("C:\\Windows\\notepad.exe");
                Everything_Query(true);
                count = Everything_GetNumResults();
            }
            if (count == 0) return 0;

            StringBuilder buf = new StringBuilder(260);
            Everything_GetResultFullPathName(0, buf, 260);
            checkedFile = buf.ToString();

            long frn = Everything_GetResultFileReferenceNumber(0);
            if (frn == 0)
            {
                uint error = Everything_GetLastError();
                Console.Error.WriteLine($"FRN 探测: 文件={checkedFile}, FRN=0, 错误码={error}");
            }
            return frn;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FRN 探测异常: {ex.Message}");
            return 0;
        }
    }
}
'@
        Add-Type -TypeDefinition $csharpCode -ReferencedAssemblies 'System' -ErrorAction Stop

        $checkedFile = ""
        $frn = [EverythingFrnProbe]::ProbeFrn([ref]$checkedFile)

        if ($frn -gt 0) {
            Write-Log -Level PASS "FRN 探测成功: 文件=$checkedFile, FRN=$frn — 墓碑精确匹配可用"
            return $true
        }
        else {
            Write-Log -Level WARN "FRN 探测失败: 文件=$checkedFile, FRN=$frn — 墓碑将降级为指纹匹配模式（3天过期）"
            return $false
        }
    }
    catch {
        Write-Log -Level ERROR "FRN 探测编译/执行异常: $($_.Exception.Message)"
        return $false
    }
}

function Wait-EverythingIndexing {
    param(
        [int]$TimeoutSeconds = 120,
        [int]$PollIntervalSeconds = 2
    )
    # 轮询 Everything 索引状态，直到完成或超时
    # Everything 不提供百分比进度，仅返回布尔值（是否正在索引）

    Write-Log -Level INFO "开始等待 Everything 索引完成（超时: ${TimeoutSeconds}秒, 轮询间隔: ${PollIntervalSeconds}秒）"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastProgressReport = $sw.Elapsed

    try {
        $csharpCode = @'
using System;
using System.Runtime.InteropServices;

public class EverythingIndexProbe
{
    [DllImport("Everything64.dll")]
    private static extern bool Everything_IsDBLoaded();

    [DllImport("Everything64.dll")]
    private static extern uint Everything_GetLastError();

    // 检查索引是否已就绪
    // 返回: 0=正在索引, 1=已就绪, -1=异常
    public static int CheckIndexing()
    {
        try
        {
            if (Everything_IsDBLoaded())
                return 1; // 索引数据库已加载 = 就绪
            uint error = Everything_GetLastError();
            Console.Error.WriteLine($"Everything_IsDBLoaded=false, 错误码: {error}");
            return 0; // 仍在索引中
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"索引检测异常: {ex.Message}");
            return -1;
        }
    }
}
'@
        Add-Type -TypeDefinition $csharpCode -ReferencedAssemblies 'System' -ErrorAction Stop

        while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            $status = [EverythingIndexProbe]::CheckIndexing()

            if ($status -eq 1) {
                $sw.Stop()
                Write-Log -Level PASS "Everything 索引已完成，等待时间: $($sw.Elapsed.TotalSeconds.ToString('F1')) 秒"
                return $true
            }

            if ($status -lt 0) {
                Write-Log -Level WARN "索引检测返回异常值，继续轮询..."
            }

            # 每 30 秒报告一次进度
            if (($sw.Elapsed - $lastProgressReport).TotalSeconds -ge 30) {
                Write-Log -Level INFO "索引仍在进行中... 已等待 $($sw.Elapsed.TotalSeconds.ToString('F0')) 秒"
                $lastProgressReport = $sw.Elapsed
            }

            Start-Sleep -Seconds $PollIntervalSeconds
        }

        $sw.Stop()
        Write-Log -Level WARN "Everything 索引等待超时（${TimeoutSeconds}秒），将进入警告模式（黄色横幅）"
        return $false
    }
    catch {
        Write-Log -Level ERROR "索引轮询异常: $($_.Exception.Message)"
        return $false
    }
}

function Install-Everything {
    # 下载并安装 Everything
    param([switch]$SkipDownload)

    if ($SkipDownload) {
        Write-Log -Level WARN "SkipDownload 开关已启用，跳过自动下载。请手动安装 Everything 1.4.1.1000+"
        return $false
    }

    $downloadUrl = 'https://www.voidtools.com/Everything-1.4.1.1026.x64.zip'
    $tempZip = "$env:TEMP\EverythingSetup_$(Get-Random).zip"
    $extractPath = "$env:ProgramFiles\Everything"

    try {
        # 步骤1: 下载
        Write-Log -Level INFO "正在下载 Everything 1.4.1.1026 (x64)..."
        Write-Log -Level INFO "下载地址: $downloadUrl"

        # 使用 TLS 1.2
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

        $webClient = New-Object System.Net.WebClient
        try {
            $webClient.DownloadFile($downloadUrl, $tempZip)
        }
        catch {
            Write-Log -Level ERROR "下载失败: $($_.Exception.Message)"
            Write-Log -Level ERROR "请手动下载: $downloadUrl 并解压到 $extractPath"
            return $false
        }
        finally {
            $webClient.Dispose()
        }

        if (-not (Test-Path $tempZip)) {
            Write-Log -Level ERROR "下载文件未生成: $tempZip"
            return $false
        }

        Write-Log -Level INFO "下载完成: $tempZip ($([math]::Round((Get-Item $tempZip).Length / 1MB, 1)) MB)"

        # 步骤2: 解压
        Write-Log -Level INFO "正在解压到 $extractPath ..."
        try {
            if (-not (Test-Path $extractPath)) {
                New-Item -ItemType Directory -Path $extractPath -Force | Out-Null
            }
            Expand-Archive -Path $tempZip -DestinationPath $extractPath -Force
            Write-Log -Level PASS "解压完成"
        }
        catch {
            Write-Log -Level ERROR "解压失败: $($_.Exception.Message)"
            return $false
        }
        finally {
            Remove-Item $tempZip -Force -ErrorAction SilentlyContinue
        }

        # 步骤3: 注册 Everything 服务
        $exePath = Join-Path $extractPath 'Everything.exe'
        if (Test-Path $exePath) {
            Write-Log -Level INFO "正在安装 Everything 服务..."
            try {
                & $exePath -install-service | Out-Null
                Start-Sleep -Seconds 2
                Write-Log -Level PASS "Everything 服务已注册"
            }
            catch {
                Write-Log -Level ERROR "服务注册失败: $($_.Exception.Message)"
                Write-Log -Level WARN "可尝试手动运行: `"$exePath`" -install-service"
            }
        }

        # 步骤4: 启动服务
        Start-EverythingService

        return $true
    }
    catch {
        Write-Log -Level ERROR "安装过程异常: $($_.Exception.Message)"
        return $false
    }
}

#endregion

#region ---- 主流程 ----

function Invoke-EverythingCheck {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  Everything 环境检测开始" -ForegroundColor Cyan
    Write-Host "  最低要求版本: $($script:MinEverythingVersion)" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""

    # ---- 步骤 0: 管理员权限检测 ----
    Write-Log -Level INFO "步骤 0: 检测管理员权限..."
    $isAdmin = Test-AdminPrivilege
    if ($isAdmin) {
        Write-Log -Level PASS "当前以管理员权限运行"
    }
    else {
        Write-Log -Level WARN "当前非管理员权限运行 — 部分操作可能受限"
    }

    # ---- 步骤 1: 检查 Everything 进程/服务是否运行 ----
    Write-Log -Level INFO "步骤 1: 检查 Everything 进程与服务..."
    $exePath = Find-EverythingInstallPath

    if (-not $exePath) {
        Write-Log -Level FAIL "Everything 未安装"
        Write-Log -Level INFO "尝试自动安装 Everything..."

        if (Test-AdminPrivilege) {
            $installed = Install-Everything -SkipDownload:$SkipDownload
            if ($installed) {
                $exePath = Find-EverythingInstallPath
            }
        }
        else {
            Write-Log -Level ERROR "需要管理员权限才能安装 Everything，请以管理员身份重新运行此脚本"
            return @{
                Success = $false
                Error  = "EverythingNotInstalled"
                Detail = "需要管理员权限安装 Everything"
            }
        }

        if (-not $exePath) {
            Write-Log -Level FAIL "Everything 安装失败或未找到安装路径"
            return @{
                Success = $false
                Error  = "EverythingNotInstalled"
                Detail = "请手动安装 Everything ≥ 1.4.1.1000: https://www.voidtools.com/"
            }
        }
    }

    $script:EverythingInstalled = $true
    Write-Log -Level PASS "Everything 安装路径: $exePath"

    # 确保服务/进程运行
    Start-EverythingService

    # ---- 步骤 2: 版本检查 ----
    Write-Log -Level INFO "步骤 2: 检查 Everything 版本..."
    $script:EverythingVersion = Get-EverythingVersion -ExePath $exePath

    if (-not $script:EverythingVersion) {
        Write-Log -Level FAIL "无法获取 Everything 版本号"
        return @{
            Success = $false
            Error  = "VersionCheckFailed"
            Detail = "无法解析 Everything.exe -version 的输出"
        }
    }

    if ($script:EverythingVersion -lt $script:MinEverythingVersion) {
        Write-Log -Level FAIL "Everything 版本不合格: $($script:EverythingVersion) < $($script:MinEverythingVersion)"
        Write-Log -Level INFO "建议升级到 1.4.1.1026: https://www.voidtools.com/downloads/"
        return @{
            Success      = $false
            Error        = "VersionTooLow"
            Detail       = "当前版本 $($script:EverythingVersion)，需要 ≥ $($script:MinEverythingVersion)"
            CurrentVersion = $script:EverythingVersion.ToString()
        }
    }

    Write-Log -Level PASS "Everything 版本合格: $($script:EverythingVersion)"

    # ---- 步骤 3: IPC 探活 ----
    Write-Log -Level INFO "步骤 3: IPC 通信检测..."
    $script:IpcAvailable = Test-EverythingIpc

    if (-not $script:IpcAvailable) {
        Write-Log -Level FAIL "Everything IPC 通信失败"
        Write-Log -Level INFO "请检查: 1) Everything 服务是否运行 2) 防火墙是否拦截 3) Everything64.dll 是否在 PATH 中"
        return @{
            Success = $false
            Error   = "IpcFailed"
            Detail  = "IPC 通信失败，请检查 Everything 服务状态和防火墙设置"
        }
    }

    Write-Log -Level PASS "Everything IPC 通信正常"

    # ---- 步骤 4: FRN 能力检测 ----
    Write-Log -Level INFO "步骤 4: FRN（文件引用号）能力检测..."
    $script:FrnAvailable = Test-EverythingFrnCapability

    # ---- 步骤 5: 索引就绪检测 ----
    Write-Log -Level INFO "步骤 5: Everything 索引就绪检测..."
    $script:IndexReady = Wait-EverythingIndexing -TimeoutSeconds $IndexingTimeoutSeconds -PollIntervalSeconds $PollIntervalSeconds

    if (-not $script:IndexReady) {
        Write-Log -Level WARN "Everything 索引未在超时时间内完成 — 进入警告模式"
        Write-Log -Level INFO "警告模式下: 扫描结果可能不完整，一键清理按钮将禁用，黄色横幅持续显示"
    }

    # ---- 汇总结果 ----
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  检测结果汇总" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  Everything 安装:  " -NoNewline
    Write-Host "✓ 通过" -ForegroundColor Green
    Write-Host "  版本:             " -NoNewline
    Write-Host "✓ $($script:EverythingVersion)" -ForegroundColor Green
    Write-Host "  IPC 通信:         " -NoNewline
    Write-Host "✓ 正常" -ForegroundColor Green
    Write-Host "  FRN 能力:         " -NoNewline
    if ($script:FrnAvailable) {
        Write-Host "✓ 可用（精确墓碑匹配）" -ForegroundColor Green
    }
    else {
        Write-Host "✗ 不可用（降级为指纹匹配，3天过期）" -ForegroundColor Yellow
    }
    Write-Host "  索引状态:         " -NoNewline
    if ($script:IndexReady) {
        Write-Host "✓ 已就绪" -ForegroundColor Green
    }
    else {
        Write-Host "⚠ 索引未完成（警告模式）" -ForegroundColor Yellow
    }
    Write-Host "============================================================" -ForegroundColor Cyan

    return @{
        Success            = $true
        EverythingPath     = $exePath
        Version            = $script:EverythingVersion.ToString()
        IpcAvailable       = $script:IpcAvailable
        FrnAvailable       = $script:FrnAvailable
        IndexReady         = $script:IndexReady
        IsAdmin            = $isAdmin
        MinVersionRequired = $script:MinEverythingVersion.ToString()
    }
}

#endregion

#region ---- 脚本入口 ----
try {
    Initialize-Logger -Path $LogPath
    $result = Invoke-EverythingCheck
    $result | ConvertTo-Json -Depth 3
    exit ($result.Success ? 0 : 1)
}
catch {
    Write-Log -Level ERROR "脚本未处理异常: $($_.Exception.Message)"
    Write-Log -Level ERROR "堆栈: $($_.ScriptStackTrace)"
    exit 2
}
finally {
    Close-Logger
}
#endregion
