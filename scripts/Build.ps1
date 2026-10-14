[CmdletBinding()]
param([string]$Dotnet = 'dotnet', [switch]$Publish)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw '请使用 PowerShell 7（pwsh）。' }
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    & $Dotnet run --project 'tests/Shige.Core.Tests' -c Release
    if ($LASTEXITCODE -ne 0) { throw '核心测试失败' }
    & $Dotnet run --project 'tests/Shige.Platform.Tests' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Windows 平台测试失败' }
    & $Dotnet build 'src/Shige.App' -c Release -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
    if ($Publish) {
        & $Dotnet publish 'src/Shige.App' -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o 'artifacts/Shige-win-x64' --nologo
        if ($LASTEXITCODE -ne 0) { throw '发布失败' }
        # Keep dependency licenses from the exact packages resolved by this build.
        $assets = Get-Content -LiteralPath 'src/Shige.App/obj/project.assets.json' -Raw | ConvertFrom-Json -AsHashtable
        $noticeRoot = Join-Path $projectRoot 'artifacts/Shige-win-x64/third-party'
        foreach ($library in $assets.libraries.Values) {
            if ($library.type -ne 'package') { continue }
            foreach ($packageFolder in $assets.packageFolders.Keys) {
                $packagePath = Join-Path $packageFolder $library.path
                if (-not (Test-Path -LiteralPath $packagePath)) { continue }
                $notices = @(Get-ChildItem -LiteralPath $packagePath -File | Where-Object Name -Match '(?i)license|notice|copyright')
                if ($notices.Count -gt 0) {
                    $destination = Join-Path $noticeRoot ($library.path.Replace('/', '-'))
                    New-Item -ItemType Directory -Path $destination -Force | Out-Null
                    foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $destination -Force }
                }
                break
            }
        }
        Copy-Item -LiteralPath 'README.md' -Destination 'artifacts/Shige-win-x64/README.md' -Force
        Copy-Item -LiteralPath 'docs/THIRD-PARTY.md' -Destination 'artifacts/Shige-win-x64/THIRD-PARTY.md' -Force
        foreach ($required in @('Shige.App.exe', 'Shige.App.pri', 'third-party/microsoft.windowsappsdk-1.8.260921001/license.txt')) {
            if (-not (Test-Path -LiteralPath (Join-Path 'artifacts/Shige-win-x64' $required))) { throw "发行包缺少必要文件：$required" }
        }
        Write-Host '发行包：artifacts/Shige-win-x64/Shige.App.exe'
    }
} finally { Pop-Location }
