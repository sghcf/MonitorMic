[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$versionFile = Join-Path $PSScriptRoot '..\VERSION'
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must have three numeric components: $Version"
}

$msbuildVersionArgs = @(
    "-p:Version=$Version"
    "-p:InformationalVersion=$Version"
    "-p:AssemblyVersion=${Version}.0"
    "-p:FileVersion=${Version}.0"
)

$publishDir = Join-Path $env:TEMP "MonitorMic-publish-${Version}-${Configuration}"
$installerProject = Join-Path $PSScriptRoot 'MonitorMicInstaller'
$installerProjectFile = Join-Path $installerProject 'MonitorMicInstaller.csproj'
$installerPublishDir = Join-Path $installerProject "bin\${Configuration}\net8.0-windows\win-x64\publish"
$required = @(Join-Path $publishDir 'MonitorMic.exe')

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Push-Location (Join-Path $repoRoot 'MonitorMicWin')
try {
    dotnet restore 'MonitorMicWin.csproj' --runtime win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "MonitorMic restore failed: $LASTEXITCODE"
    }
    $appPublishDirArg = "-p:PublishDir=${publishDir}\"
    dotnet publish 'MonitorMicWin.csproj' --configuration $Configuration --runtime win-x64 --self-contained false --no-restore -p:PublishSingleFile=true $appPublishDirArg @msbuildVersionArgs
    if ($LASTEXITCODE -ne 0) {
        throw "MonitorMic publish failed: $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing publish input: $path"
    }
}

$staging = Join-Path $env:TEMP 'MonitorMic-installer-staging'
$payload = Join-Path $staging 'payload'
$payloadZip = Join-Path $staging 'MonitorMicPayload.zip'
$embeddedPayload = Join-Path $installerProject 'MonitorMicPayload.zip'
$finalInstaller = Join-Path $repoRoot "MonitorMicWin\MonitorMicSetup-${Version}.exe"

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $payload | Out-Null

Copy-Item -Path (Join-Path $publishDir '*') -Destination $payload -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.txt') -Destination $payload -Force
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $payloadZip -CompressionLevel Optimal
Copy-Item -LiteralPath $payloadZip -Destination $embeddedPayload -Force

Push-Location $installerProject
try {
    dotnet restore $installerProjectFile --runtime win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "Installer restore failed: $LASTEXITCODE"
    }

    if (Test-Path -LiteralPath $installerPublishDir) {
        Remove-Item -LiteralPath $installerPublishDir -Recurse -Force
    }
    $installerPublishDirArg = "-p:PublishDir=${installerPublishDir}\"
    dotnet publish $installerProjectFile --configuration $Configuration --runtime win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true $installerPublishDirArg @msbuildVersionArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Installer publish failed: $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$builtInstaller = Join-Path $installerPublishDir 'MonitorMicSetup.exe'
if (-not (Test-Path -LiteralPath $builtInstaller)) {
    throw "Installer output missing: $builtInstaller"
}
Copy-Item -LiteralPath $builtInstaller -Destination $finalInstaller -Force
$file = Get-Item -LiteralPath $finalInstaller
if ($file.Length -lt 100KB) {
    throw "Generated installer is unexpectedly small: $($file.Length) bytes"
}
$magic = [System.IO.File]::ReadAllBytes($finalInstaller)[0..1]
if ($magic[0] -ne 0x4D -or $magic[1] -ne 0x5A) {
    throw 'Generated file is not a valid Windows PE executable.'
}

Write-Output "Installer: $($file.FullName)"
Write-Output "Size: $($file.Length) bytes"
Write-Output "SHA256: $((Get-FileHash -LiteralPath $finalInstaller -Algorithm SHA256).Hash)"
