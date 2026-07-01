# ============================================================
# Generate-TestVolume.ps1
# 功能: 轻量版测试卷生成（1-5万文件，用于快速冒烟测试）
# 与 TestVolumeGenerator.exe 配合：200万文件用 C# 工具，快速验证用此脚本
# 对应任务: D1.5 — 生成含测试文件的 NTFS 测试卷
# ============================================================

param(
    [Parameter(Mandatory=$true)]
    [string]$VolumeRoot,                          # 测试卷根目录

    [int]$FileCount = 10000,                      # 目标文件数量

    [ValidateSet('quick', 'rule', 'decision')]
    [string]$Profile = 'quick',                   # 生成策略

    [string]$LogPath = "$env:TEMP\TestVolumeGen_$(Get-Date -Format 'yyyyMMdd_HHmmss').log",

    [switch]$CleanExisting = $false               # 是否先删除已有目录
)

#region ---- 初始化 ----
$script:LogStream = $null
$script:StartTime = Get-Date
$script:CreatedFiles = 0
$script:ErrorFiles = 0

function Initialize-Logger {
    param([string]$Path)
    try {
        $logDir = Split-Path $Path -Parent
        if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        $script:LogStream = [System.IO.StreamWriter]::new($Path, $true, [System.Text.Encoding]::UTF8)
        Write-Log "测试卷生成脚本启动 | 目标: $FileCount 个文件 | 策略: $Profile | 根目录: $VolumeRoot"
    }
    catch {
        Write-Warning "无法创建日志文件: $($_.Exception.Message)"
    }
}

function Write-Log {
    param([string]$Level='INFO', [string]$Message)
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')] [$Level] $Message"
    switch ($Level) {
        'ERROR' { Write-Host $line -ForegroundColor Red }
        'WARN'  { Write-Host $line -ForegroundColor Yellow }
        'PASS'  { Write-Host $line -ForegroundColor Green }
        default { Write-Host $line }
    }
    if ($script:LogStream) { try { $script:LogStream.WriteLine($line); $script:LogStream.Flush() } catch {} }
}

function Close-Logger {
    if ($script:LogStream) {
        $elapsed = (Get-Date) - $script:StartTime
        Write-Log "生成完成: 成功=$script:CreatedFiles, 失败=$script:ErrorFiles, 耗时=$($elapsed.TotalSeconds.ToString('F1'))秒"
        try { $script:LogStream.Close(); $script:LogStream.Dispose() } catch {}
    }
}
#endregion

#region ---- 文件分布配置 ----

# 模拟真实 Windows 目录结构
$DirectoryStructure = @(
    @{Path='Windows\System32'; Ratio=0.08; Extensions=@('.dll','.exe','.sys','.mui'); Protected=$true},
    @{Path='Windows\Temp'; Ratio=0.15; Extensions=@('.tmp','.log','.etl'); Protected=$false},
    @{Path='Windows\Logs'; Ratio=0.10; Extensions=@('.log','.etl','.evtx'); Protected=$false},
    @{Path='Program Files\AppX\cache'; Ratio=0.08; Extensions=@('.cache','.dat','.db'); Protected=$false},
    @{Path='Program Files (x86)\AppY\temp'; Ratio=0.07; Extensions=@('.tmp','.cache'); Protected=$false},
    @{Path='Users\TestUser\AppData\Local\Temp'; Ratio=0.15; Extensions=@('.tmp','.log','.cache','.dmp'); Protected=$false},
    @{Path='Users\TestUser\AppData\Local\Google\Chrome\User Data\Default\Cache'; Ratio=0.10; Extensions=@('.dat','.cache'); Protected=$false},
    @{Path='Users\TestUser\AppData\Local\Microsoft\Edge\User Data\Default\Cache'; Ratio=0.07; Extensions=@('.dat','.cache'); Protected=$false},
    @{Path='Users\TestUser\Downloads'; Ratio=0.10; Extensions=@('.zip','.msi','.exe','.pdf','.iso'); Protected=$false},
    @{Path='Users\TestUser\Documents'; Ratio=0.05; Extensions=@('.docx','.xlsx','.pptx','.pdf'); Protected=$false},
    @{Path='ProgramData\Package Cache'; Ratio=0.05; Extensions=@('.cab','.msi','.msu'); Protected=$false}
)

# 文件大小分布（字节）
$SizeDistribution = @(
    @{Min=1024; Max=10KB; Ratio=0.25},       # 1KB-10KB: 25%
    @{Min=10KB; Max=100KB; Ratio=0.30},      # 10KB-100KB: 30%
    @{Min=100KB; Max=1MB; Ratio=0.25},       # 100KB-1MB: 25%
    @{Min=1MB; Max=10MB; Ratio=0.15},        # 1MB-10MB: 15%
    @{Min=10MB; Max=100MB; Ratio=0.05}       # 10MB-100MB: 5%
)

# 文件年龄分布（天）
$AgeDistribution = @(
    @{Min=0; Max=7; Ratio=0.30},             # 今天-7天: 30%
    @{Min=7; Max=30; Ratio=0.30},            # 7-30天: 30%
    @{Min=30; Max=90; Ratio=0.20},           # 30-90天: 20%
    @{Min=90; Max=365; Ratio=0.15},          # 90-365天: 15%
    @{Min=365; Max=730; Ratio=0.05}          # 1-2年: 5%
)
#endregion

#region ---- 核心生成逻辑 ----

function Get-RandomExtension {
    param([string[]]$Extensions)
    return $Extensions[(Get-Random -Minimum 0 -Maximum $Extensions.Count)]
}

function Get-RandomSize {
    # 根据 SizeDistribution 加权随机选择文件大小
    $rand = Get-Random -Minimum 0.0 -Maximum 1.0
    $cumulative = 0.0
    foreach ($bucket in $SizeDistribution) {
        $cumulative += $bucket.Ratio
        if ($rand -le $cumulative) {
            return Get-Random -Minimum $bucket.Min -Maximum $bucket.Max
        }
    }
    return 1024 # 默认 1KB
}

function Get-RandomAge {
    # 根据 AgeDistribution 加权随机选择文件年龄（天）
    $rand = Get-Random -Minimum 0.0 -Maximum 1.0
    $cumulative = 0.0
    foreach ($bucket in $AgeDistribution) {
        $cumulative += $bucket.Ratio
        if ($rand -le $cumulative) {
            $days = Get-Random -Minimum $bucket.Min -Maximum $bucket.Max
            return (Get-Date).AddDays(-$days)
        }
    }
    return (Get-Date).AddDays(-30) # 默认 30 天前
}

function New-TestFile {
    param(
        [string]$FullPath,
        [long]$Size,
        [DateTime]$LastWriteTime
    )
    try {
        # 确保父目录存在
        $parentDir = Split-Path $FullPath -Parent
        if (-not (Test-Path $parentDir)) {
            [System.IO.Directory]::CreateDirectory($parentDir)
        }

        # 创建文件并用稀疏策略写入
        # 小文件（<1MB）：写入随机内容
        # 大文件（≥1MB）：WriteAllBytes + SetLength 扩展（稀疏区域为零）
        if ($Size -le 1048576) {
            # 小文件：生成实际随机内容
            $buffer = New-Object byte[] $Size
            (New-Object Random).NextBytes($buffer)
            [System.IO.File]::WriteAllBytes($FullPath, $buffer)
        }
        else {
            # 大文件：仅写入首尾 64KB 随机数据，其余区域为 NTFS 稀疏区域
            $headerSize = 65536 # 64KB
            $footerSize = 65536 # 64KB

            $header = New-Object byte[] $headerSize
            $footer = New-Object byte[] $footerSize
            $rng = New-Object Random
            $rng.NextBytes($header)
            $rng.NextBytes($footer)

            # 创建文件并写入文件头
            [System.IO.File]::WriteAllBytes($FullPath, $header)

            # 扩展到目标大小（中间区域为稀疏/零填充）
            $fs = [System.IO.File]::OpenWrite($FullPath)
            try {
                $fs.Seek($Size - $footerSize, [System.IO.SeekOrigin]::Begin) | Out-Null
                $fs.Write($footer, 0, $footerSize)
            }
            finally {
                $fs.Close()
            }
        }

        # 设置文件时间戳
        try {
            [System.IO.File]::SetCreationTime($FullPath, $LastWriteTime)
            [System.IO.File]::SetLastWriteTime($FullPath, $LastWriteTime)
            [System.IO.File]::SetLastAccessTime($FullPath, (Get-Date).AddDays(-(Get-Random -Minimum 0 -Maximum 3)))
        }
        catch {
            # 时间戳设置失败不阻断流程
        }

        return $true
    }
    catch {
        Write-Log -Level ERROR "创建文件失败: $FullPath — $($_.Exception.Message)"
        return $false
    }
}

function Invoke-TestVolumeGeneration {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  测试卷生成开始" -ForegroundColor Cyan
    Write-Host "  根目录: $VolumeRoot" -ForegroundColor White
    Write-Host "  目标文件数: $FileCount" -ForegroundColor White
    Write-Host "  策略: $Profile" -ForegroundColor White
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""

    # 验证根目录
    if (-not (Test-Path $VolumeRoot)) {
        try {
            [System.IO.Directory]::CreateDirectory($VolumeRoot)
            Write-Log -Level INFO "创建测试卷根目录: $VolumeRoot"
        }
        catch {
            Write-Log -Level ERROR "无法创建根目录: $VolumeRoot — $($_.Exception.Message)"
            return $false
        }
    }

    # 检查目标磁盘是否为 NTFS
    $driveInfo = New-Object System.IO.DriveInfo((Split-Path $VolumeRoot -Qualifier))
    if ($driveInfo.DriveFormat -ne 'NTFS') {
        Write-Log -Level WARN "目标磁盘格式为 $($driveInfo.DriveFormat)，非 NTFS。虽然不阻断生成，但 Everything 仅索引 NTFS 卷。"
    }
    else {
        Write-Log -Level PASS "目标磁盘为 NTFS，符合项目要求"
    }

    # 可选：清理已有文件
    if ($CleanExisting) {
        Write-Log -Level WARN "正在清理已有测试卷内容: $VolumeRoot ..."
        try {
            Get-ChildItem -Path $VolumeRoot -Recurse -File | ForEach-Object {
                Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
            }
            Write-Log -Level INFO "清理完成"
        }
        catch {
            Write-Log -Level WARN "清理过程中出现错误: $($_.Exception.Message)"
        }
    }

    # 生成文件
    Write-Log -Level INFO "开始生成文件..."
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastReport = $sw.Elapsed

    for ($i = 0; $i -lt $FileCount; $i++) {
        # 加权随机选择目录
        $rand = Get-Random -Minimum 0.0 -Maximum 1.0
        $cumulative = 0.0
        $selectedDir = $DirectoryStructure[0]
        foreach ($dir in $DirectoryStructure) {
            $cumulative += $dir.Ratio
            if ($rand -le $cumulative) {
                $selectedDir = $dir
                break
            }
        }

        # 生成文件名: GUID前缀_序号.扩展名
        $ext = Get-RandomExtension -Extensions $selectedDir.Extensions
        $fileName = "{0}_{1:D6}{2}" -f [Guid]::NewGuid().ToString('N').Substring(0, 8), $i, $ext
        $filePath = Join-Path $VolumeRoot $selectedDir.Path $fileName

        # 随机决定文件大小和年龄
        $size = Get-RandomSize
        $lastWriteTime = Get-RandomAge

        # 创建文件
        if (New-TestFile -FullPath $filePath -Size $size -LastWriteTime $lastWriteTime) {
            $script:CreatedFiles++
        }
        else {
            $script:ErrorFiles++
        }

        # 每 1000 个文件报告一次进度
        if ($i -gt 0 -and $i % 1000 -eq 0) {
            $elapsed = $sw.Elapsed.TotalSeconds
            $rate = [math]::Round($i / $elapsed, 1)
            $eta = [math]::Round(($FileCount - $i) / $rate, 0)
            Write-Log -Level INFO "进度: $i / $FileCount ($([math]::Round($i/$FileCount*100,1))%) | 速度: $rate 文件/秒 | 预计剩余: ${eta}秒"
        }

        # 每 5000 个文件让系统喘息
        if ($i % 5000 -eq 0 -and $i -gt 0) {
            Start-Sleep -Milliseconds 100
        }
    }

    $sw.Stop()
    Write-Log -Level PASS "文件生成完成: $script:CreatedFiles 成功 / $script:ErrorFiles 失败"
    Write-Log -Level INFO "总耗时: $($sw.Elapsed.TotalSeconds.ToString('F1')) 秒"
    Write-Log -Level INFO "平均速度: $([math]::Round($script:CreatedFiles / $sw.Elapsed.TotalSeconds, 1)) 文件/秒"

    return ($script:ErrorFiles -eq 0)
}

#endregion

#region ---- 脚本入口 ----
try {
    Initialize-Logger -Path $LogPath
    $success = Invoke-TestVolumeGeneration
    if ($success) {
        Write-Host ""
        Write-Host "✓ 测试卷生成成功！" -ForegroundColor Green
        Write-Host "  根目录: $VolumeRoot" -ForegroundColor White
        Write-Host "  文件数: $script:CreatedFiles" -ForegroundColor White
    }
    exit ($success ? 0 : 1)
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
