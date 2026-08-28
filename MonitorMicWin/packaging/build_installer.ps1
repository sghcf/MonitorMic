[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$publishDir = Join-Path $repoRoot "MonitorMicWin\bin\$Configuration\net8.0-windows\win-x64\publish-selfcontained"
$installerProject = Join-Path $PSScriptRoot 'MonitorMicInstaller'
$installerProjectFile = Join-Path $installerProject 'MonitorMicInstaller.csproj'
$installerPublishDir = Join-Path $installerProject "bin\$Configuration\net8.0-windows\win-x64\publish"
$required = @(
    (Join-Path $publishDir 'MonitorMic.exe'),
    (Join-Path $repoRoot 'MonitorMicWin\micstreamer.apk'),
    (Join-Path $repoRoot 'MonitorMicWin\adb\adb.exe'),
    (Join-Path $repoRoot 'MonitorMicWin\driver\VBCABLE_Setup_x64.exe')
)

foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "缺少打包输入：$path。请先完成 self-contained publish 和运行时资源准备。"
    }
}

$staging = Join-Path $env:TEMP 'MonitorMic-installer-staging'
$payload = Join-Path $staging 'payload'
$payloadZip = Join-Path $staging 'MonitorMicPayload.zip'
$embeddedPayload = Join-Path $installerProject 'MonitorMicPayload.zip'
$finalInstaller = Join-Path $repoRoot 'MonitorMicWin\MonitorMicSetup-1.2.1.exe'

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $payload | Out-Null

Copy-Item -Path (Join-Path $publishDir '*') -Destination $payload -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'MonitorMicWin\micstreamer.apk') -Destination $payload -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'MonitorMicWin\adb') -Destination (Join-Path $payload 'adb') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'MonitorMicWin\driver') -Destination (Join-Path $payload 'driver') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.txt') -Destination $payload -Force

Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $payloadZip -CompressionLevel Optimal
Copy-Item -LiteralPath $payloadZip -Destination $embeddedPayload -Force

Push-Location $installerProject
try {
    dotnet restore 'MonitorMicInstaller.csproj' --runtime win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "安装器项目 restore 失败，退出码：$LASTEXITCODE"
    }

    if (Test-Path -LiteralPath $installerPublishDir) {
        Remove-Item -LiteralPath $installerPublishDir -Recurse -Force
    }
    dotnet publish 'MonitorMicInstaller.csproj' --configuration $Configuration --runtime win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishDir="bin\$Configuration\net8.0-windows\win-x64\publish\"
    if ($LASTEXITCODE -ne 0) {
        throw "安装器 publish 失败，退出码：$LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$builtInstaller = Join-Path $installerPublishDir 'MonitorMicSetup.exe'
if (-not (Test-Path -LiteralPath $builtInstaller)) {
    throw "安装器输出不存在：$builtInstaller"
}
Copy-Item -LiteralPath $builtInstaller -Destination $finalInstaller -Force
$file = Get-Item -LiteralPath $finalInstaller
if ($file.Length -lt 100KB) {
    throw "生成的安装包异常过小：$($file.Length) bytes"
}
$magic = Get-Content -LiteralPath $finalInstaller -AsByteStream -TotalCount 2
if ($magic[0] -ne 0x4D -or $magic[1] -ne 0x5A) {
    throw '生成的文件不是有效的 Windows PE/EXE 文件。'
}

Write-Output "安装包：$($file.FullName)"
Write-Output "大小：$($file.Length) bytes"
Write-Output "SHA256：$((Get-FileHash -LiteralPath $finalInstaller -Algorithm SHA256).Hash)"
