[CmdletBinding()]
param([string]$Executable, [switch]$Isolated)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) {
    $Executable = Join-Path $projectRoot 'artifacts/Shige-win-x64/Shige.App.exe'
    if (-not (Test-Path -LiteralPath $Executable)) { $Executable = Join-Path $projectRoot 'src/Shige.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/Shige.App.exe' }
}
if (-not (Test-Path -LiteralPath $Executable)) { throw '找不到程序，请先构建或指定 -Executable。' }
if ($Isolated) {
    $learningRoot = Join-Path $projectRoot 'artifacts/learning'
    $env:SHIGE_DATA_DIR = Join-Path $learningRoot 'settings'
    $env:SHIGE_STORAGE_ROOT = Join-Path $learningRoot 'files'
    $env:SHIGE_DESKTOP_DIR = Join-Path $learningRoot 'desktop'
    New-Item -ItemType Directory -Force -Path $env:SHIGE_DATA_DIR,$env:SHIGE_STORAGE_ROOT,$env:SHIGE_DESKTOP_DIR | Out-Null
    Write-Host "学习桌面：$env:SHIGE_DESKTOP_DIR（可放测试文件）"
}
Write-Host '若已有拾格运行，请先从托盘退出，新的环境设置才会生效。'
Start-Process -FilePath (Resolve-Path -LiteralPath $Executable).Path -WindowStyle Normal
