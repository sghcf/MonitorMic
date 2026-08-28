[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$publishDir = Join-Path $repoRoot "MonitorMicWin\bin\$Configuration\net8.0-windows\win-x64\publish-selfcontained"
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
$sedPath = Join-Path $env:TEMP 'MonitorMicSetup.sed'
$builtInstaller = Join-Path $env:TEMP 'MonitorMicSetup-1.2.1.exe'
$finalInstaller = Join-Path $repoRoot 'MonitorMicWin\MonitorMicSetup-1.2.1.exe'
$iexpress = Join-Path $env:WINDIR 'System32\iexpress.exe'

if (-not (Test-Path -LiteralPath $iexpress)) {
    throw "Windows IExpress 不存在：$iexpress"
}

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $payload | Out-Null

Copy-Item -Path (Join-Path $publishDir '*') -Destination $payload -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'MonitorMicWin\micstreamer.apk') -Destination $payload -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'MonitorMicWin\adb') -Destination (Join-Path $payload 'adb') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'MonitorMicWin\driver') -Destination (Join-Path $payload 'driver') -Recurse -Force

if (Test-Path -LiteralPath $payloadZip) {
    Remove-Item -LiteralPath $payloadZip -Force
}
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $payloadZip -CompressionLevel Optimal
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $staging -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.txt') -Destination $staging -Force

if (Test-Path -LiteralPath $sedPath) {
    Remove-Item -LiteralPath $sedPath -Force
}
if (Test-Path -LiteralPath $builtInstaller) {
    Remove-Item -LiteralPath $builtInstaller -Force
}

$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3

[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
HideExtractDialog=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles

[Strings]
InstallPrompt=Start MonitorMic installation?
FinishMessage=MonitorMic installation completed.
TargetName=$builtInstaller
FriendlyName=MonitorMic Windows 1.2.1
AppLaunched=PowerShell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File install.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
FILE0="install.ps1"
FILE1="MonitorMicPayload.zip"
FILE2="THIRD_PARTY_NOTICES.txt"

[SourceFiles]
SourceFiles0=$staging\

[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
"@
Set-Content -LiteralPath $sedPath -Value $sed -Encoding ASCII

$iexpressProcess = Start-Process -FilePath $iexpress -ArgumentList @('/N', '/Q', $sedPath) -Wait -PassThru
if ($iexpressProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $builtInstaller)) {
    throw "IExpress 打包失败，退出码：$($iexpressProcess.ExitCode)"
}

Copy-Item -LiteralPath $builtInstaller -Destination $finalInstaller -Force
$file = Get-Item -LiteralPath $finalInstaller
if ($file.Length -lt 100KB) {
    throw "生成的安装包异常过小：$($file.Length) bytes"
}
$magic = (Get-Content -LiteralPath $finalInstaller -AsByteStream -TotalCount 2)
if ($magic[0] -ne 0x4D -or $magic[1] -ne 0x5A) {
    throw '生成的文件不是有效的 Windows PE/EXE 文件。'
}

Write-Output "安装包：$($file.FullName)"
Write-Output "大小：$($file.Length) bytes"
Write-Output "SHA256：$((Get-FileHash -LiteralPath $finalInstaller -Algorithm SHA256).Hash)"
