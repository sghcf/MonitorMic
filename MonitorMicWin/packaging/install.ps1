$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\MonitorMic'
$payloadZip = Join-Path $PSScriptRoot 'MonitorMicPayload.zip'
$unpackDir = Join-Path $env:TEMP ('MonitorMicInstall-' + [guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force -Path $unpackDir | Out-Null
    Expand-Archive -LiteralPath $payloadZip -DestinationPath $unpackDir -Force
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Copy-Item -Path (Join-Path $unpackDir '*') -Destination $installDir -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.txt') -Destination $installDir -Force

    $exe = Join-Path $installDir 'MonitorMic.exe'
    $driver = Join-Path $installDir 'driver\VBCABLE_Setup_x64.exe'

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name 'MonitorMic' -Value ('"' + $exe + '" --minimized')

    $shell = New-Object -ComObject WScript.Shell
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\MonitorMic.lnk'
    $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'MonitorMic.lnk'
    foreach ($shortcutPath in @($startMenu, $desktop)) {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = $installDir
        $shortcut.IconLocation = $exe
        $shortcut.Save()
    }

    if (Test-Path -LiteralPath $driver) {
        $answer = [System.Windows.Forms.MessageBox]::Show(
            'MonitorMic 需要安装 VB-CABLE 虚拟声卡才能作为系统麦克风使用。现在安装并接受随后出现的 UAC 提示吗？',
            'MonitorMic - 安装 VB-CABLE',
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Information)
        if ($answer -eq [System.Windows.Forms.DialogResult]::Yes) {
            Start-Process -FilePath $driver -WorkingDirectory (Split-Path $driver) -Verb RunAs -Wait
            [System.Windows.Forms.MessageBox]::Show(
                'VB-CABLE 安装程序已运行。若 Windows 要求重启，请重启后再启动 MonitorMic。',
                'MonitorMic',
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
        }
    }

    Start-Process -FilePath $exe -WorkingDirectory $installDir
}
catch {
    [System.Windows.Forms.MessageBox]::Show(
        ('MonitorMic 安装失败：' + $_.Exception.Message),
        'MonitorMic',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    exit 1
}
finally {
    if (Test-Path -LiteralPath $unpackDir) {
        Remove-Item -LiteralPath $unpackDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
