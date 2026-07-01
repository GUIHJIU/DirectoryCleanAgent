# ============================================================
# Setup-TestEnvironment.ps1
# 功能: 主编排脚本 — 依次执行 Everything 环境检测 + 测试卷生成
# 对应任务: D1.5 — 一键搭建完整性能与集成测试环境
# ============================================================

param(
    [string]$VolumeRoot = "$env:TEMP\SpaceClearTestVolume",   # 测试卷根目录

    [ValidateSet('quick', 'full')]
    [string]$Mode = 'quick',                                   # quick=轻量卷, full=200万文件(需C#工具)

    [int]$QuickFileCount = 10000,                              # quick模式文件数

    [string]$EverythingScript = "$PSScriptRoot\Check-EverythingEnvironment.ps1",
    [string]$VolumeScript = "$PSScriptRoot\Generate-TestVolume.ps1",

    [string]$LogDir = "$env:TEMP\SpaceClearTestLogs",

    [switch]$SkipEverything = $false,                          # 跳过 Everything 检测
    [switch]$SkipVolumeGen = $false                            # 跳过测试卷生成
)

$script:StartTime = Get-Date
$script:LogFile = Join-Path $LogDir "Setup_$(Get-Date -Format 'yyyyMMdd_HHmmss').log"
$script:Results = @{}

#region ---- 辅助函数 ----

function Initialize-Environment {
    # 创建日志目录
    if (-not (Test-Path $LogDir)) {
        New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    }

    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  D1.5 性能与集成测试环境搭建" -ForegroundColor Cyan
    Write-Host "  模式: $Mode" -ForegroundColor White
    Write-Host "  日志目录: $LogDir" -ForegroundColor White
    Write-Host "  启动时间: $($script:StartTime.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor White
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""

    # 系统环境信息收集
    Write-Host "系统环境:" -ForegroundColor Gray
    Write-Host "  操作系统: $([System.Environment]::OSVersion.VersionString)" -ForegroundColor Gray
    Write-Host "  .NET 版本: $(dotnet --version 2>$null)" -ForegroundColor Gray
    Write-Host "  处理器数: $([System.Environment]::ProcessorCount)" -ForegroundColor Gray
    Write-Host "  内存: $([math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)) GB" -ForegroundColor Gray
    Write-Host ""
}

function Invoke-Step {
    param(
        [string]$StepName,
        [ScriptBlock]$Action,
        [bool]$Skip = $false
    )

    if ($Skip) {
        Write-Host "[跳过] $StepName" -ForegroundColor Yellow
        $script:Results[$StepName] = "SKIPPED"
        return $null
    }

    Write-Host "[执行] $StepName..." -ForegroundColor Yellow
    try {
        $result = & $Action
        $script:Results[$StepName] = "PASS"
        Write-Host "[通过] $StepName" -ForegroundColor Green

        # 如果结果是 Hashtable 且包含详细信息，则打印
        if ($result -is [hashtable]) {
            foreach ($key in $result.Keys) {
                Write-Host "    $key : $($result[$key])" -ForegroundColor Gray
            }
        }
        return $result
    }
    catch {
        $script:Results[$StepName] = "FAIL"
        Write-Host "[失败] $StepName — $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "   堆栈: $($_.ScriptStackTrace)" -ForegroundColor DarkRed
        return $null
    }
}

function Get-FinalReport {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  D1.5 环境搭建结果汇总" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan

    $allPassed = $true
    foreach ($key in $script:Results.Keys) {
        $status = $script:Results[$key]
        $color = if ($status -eq 'PASS') { 'Green' }
                 elseif ($status -eq 'SKIPPED') { 'Yellow' }
                 else { 'Red'; $allPassed = $false }
        Write-Host "  [$status] $key" -ForegroundColor $color
    }

    $elapsed = (Get-Date) - $script:StartTime
    Write-Host ""
    Write-Host "  总耗时: $($elapsed.TotalMinutes.ToString('F1')) 分钟" -ForegroundColor White
    Write-Host "  日志文件: $script:LogFile" -ForegroundColor White
    Write-Host "============================================================" -ForegroundColor Cyan

    return $allPassed
}

#endregion

#region ---- 步骤实现 ----

# 步骤 1: Everything 环境检测
function Run-EverythingCheck {
    if (-not (Test-Path $EverythingScript)) {
        Write-Error "Everything 检测脚本不存在: $EverythingScript"
        return $false
    }

    Write-Host "--- Everything 环境检测 ---" -ForegroundColor Gray
    $logFile = Join-Path $LogDir "Everything_$(Get-Date -Format 'yyyyMMdd_HHmmss').log"
    $result = & $EverythingScript -LogPath $logFile

    if ($LASTEXITCODE -ne 0) {
        throw "Everything 环境检测失败，退出码: $LASTEXITCODE"
    }

    Write-Host "  Everything 日志: $logFile" -ForegroundColor Gray
    return $result
}

# 步骤 2: 测试卷生成
function Run-VolumeGeneration {
    if ($Mode -eq 'quick') {
        # 轻量模式：PowerShell 脚本
        if (-not (Test-Path $VolumeScript)) {
            Write-Error "测试卷生成脚本不存在: $VolumeScript"
            return $false
        }

        Write-Host "--- 测试卷生成（轻量模式: $QuickFileCount 文件）---" -ForegroundColor Gray
        $logFile = Join-Path $LogDir "Volume_$(Get-Date -Format 'yyyyMMdd_HHmmss').log"
        & $VolumeScript -VolumeRoot $VolumeRoot -FileCount $QuickFileCount -Profile 'quick' -LogPath $logFile

        if ($LASTEXITCODE -ne 0) {
            throw "测试卷生成失败，退出码: $LASTEXITCODE"
        }

        Write-Host "  卷生成日志: $logFile" -ForegroundColor Gray
        Write-Host "  测试卷根目录: $VolumeRoot" -ForegroundColor White
        return $true
    }
    else {
        # 完整模式：C# 控制台工具
        $toolProject = Join-Path $PSScriptRoot '..' 'tools' 'TestVolumeGenerator' 'TestVolumeGenerator.csproj'

        if (-not (Test-Path $toolProject)) {
            throw "TestVolumeGenerator 项目不存在: $toolProject`n请先构建 C# 测试卷生成工具"
        }

        Write-Host "--- 测试卷生成（完整模式: 200万文件，使用 C# 工具）---" -ForegroundColor Gray
        Write-Host "  此过程可能需要 30-60 分钟..." -ForegroundColor Yellow

        # 先构建工具
        Write-Host "  构建 TestVolumeGenerator..." -ForegroundColor Gray
        $buildResult = dotnet build $toolProject -c Release 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "TestVolumeGenerator 构建失败: $buildResult"
        }

        # 运行工具
        Write-Host "  运行 TestVolumeGenerator（200万文件）..." -ForegroundColor Gray
        $runResult = dotnet run --project $toolProject -c Release -- --root $VolumeRoot --file-count 2000000 --profile realistic --seed 42 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "TestVolumeGenerator 运行失败: $runResult"
        }

        Write-Host "  测试卷根目录: $VolumeRoot" -ForegroundColor White
        return $true
    }
}

#endregion

#region ---- 主流程 ----
try {
    Initialize-Environment

    # 步骤 1: Everything 环境检测
    $everythingResult = Invoke-Step -StepName 'Everything环境检测' `
                                    -Action { Run-EverythingCheck } `
                                    -Skip:$SkipEverything

    if (-not $everythingResult -and -not $SkipEverything) {
        Write-Host ""
        Write-Host "⚠ Everything 环境检测未通过，但继续后续步骤（测试卷生成不依赖 Everything）" -ForegroundColor Yellow
        Write-Host "  集成测试中以 EverythingTestHelper.IsAvailable 运行时判断是否启用 Everything 相关测试" -ForegroundColor Yellow
    }

    # 步骤 2: 测试卷生成
    Invoke-Step -StepName '测试卷生成' `
                -Action { Run-VolumeGeneration } `
                -Skip:$SkipVolumeGen

    # 最终报告
    $allPassed = Get-FinalReport
    exit ($allPassed ? 0 : 1)
}
catch {
    Write-Host ""
    Write-Host "[致命错误] $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "堆栈: $($_.ScriptStackTrace)" -ForegroundColor DarkRed
    exit 2
}
#endregion
