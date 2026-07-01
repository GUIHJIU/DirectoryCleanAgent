# ============================================================
# Download-EverythingPortable.ps1
# 功能: 下载 Everything 便携版 ZIP 到 lib/Everything-Portable/
# 用途: 开发阶段测试捆绑安装功能，MSI 打包时作为 Redist 备份
# ============================================================
param(
    [string]$OutputDir = "lib\Everything-Portable",
    [string]$Version = "1.4.1.1026"
)

$ErrorActionPreference = "Stop"

# 计算脚本目录（即 scripts/ 目录）
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# 输出到项目根目录下的 lib/Everything-Portable
$ProjectRoot = Split-Path -Parent $ScriptDir
$OutputPath = Join-Path $ProjectRoot $OutputDir

# 确保输出目录存在
if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
}

$zipUrl = "https://www.voidtools.com/Everything-$Version.x64.zip"
$zipFile = Join-Path $OutputPath "Everything-$Version.x64.zip"

Write-Host "Downloading Everything $Version (x64)..." -ForegroundColor Cyan
Write-Host "  URL: $zipUrl" -ForegroundColor White
Write-Host "  Dest: $zipFile" -ForegroundColor Gray

# TLS 1.2 required by voidtools
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$wc = New-Object System.Net.WebClient
try {
    $wc.DownloadFile($zipUrl, $zipFile)
    $size = (Get-Item $zipFile).Length / 1MB
    Write-Host "Downloaded: $([math]::Round($size, 1)) MB" -ForegroundColor Green

    # 解压校验
    $extractDir = Join-Path $OutputPath "x64"
    if (Test-Path $extractDir) {
        Remove-Item -Recurse -Force $extractDir
    }
    Expand-Archive -Path $zipFile -DestinationPath $extractDir -Force
    Write-Host "Extracted to: $extractDir" -ForegroundColor Green
    Write-Host "Contents:" -ForegroundColor Gray
    Get-ChildItem $extractDir | Select-Object Name, Length | Format-Table -AutoSize

    Write-Host ""
    Write-Host "To use as MSI Redist: copy $zipFile to src\DirectoryCleanAgent\Redist\Everything-Portable.zip" -ForegroundColor Yellow
}
catch {
    Write-Host "Download failed: $_" -ForegroundColor Red
    exit 1
}
finally {
    $wc.Dispose()
}
