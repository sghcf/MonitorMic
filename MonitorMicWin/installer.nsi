; ============================================================
; MonitorMic for Windows 安装包 (NSIS)
; 每用户安装：无需管理员权限，双击即用
; 版本由 MonitorMicWin/VERSION 传入 /DAPP_VERSION=...
; ============================================================
!define APP_NAME "MonitorMic"
!ifndef APP_VERSION
!error "APP_VERSION must be supplied from MonitorMicWin/VERSION"
!endif
!define APP_EXE "MonitorMic.exe"

Unicode true
Name "${APP_NAME}"
OutFile "MonitorMicSetup-${APP_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
RequestExecutionLevel user
ShowInstDetails show

!include "MUI2.nsh"
!define MUI_ICON "app.ico"
!define MUI_UNICON "app.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 ${APP_NAME}（常驻托盘）"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Section "Install"
    ; 结束可能正在运行的旧实例，避免覆盖文件失败
    nsExec::Exec 'taskkill /f /im MonitorMic.exe'
    Sleep 1000

    SetOutPath "$INSTDIR"
    File "publish\MonitorMic.exe"

    CreateShortcut "$SMPROGRAMS\MonitorMic.lnk" "$INSTDIR\${APP_EXE}"
    CreateShortcut "$DESKTOP\MonitorMic.lnk" "$INSTDIR\${APP_EXE}"

    ; 开机自启（托盘模式）
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" \
        "MonitorMic" '"$INSTDIR\${APP_EXE}" --minimized'

    ; 卸载注册信息（出现在"设置→应用"里）
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorMic" \
        "DisplayName" "${APP_NAME}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorMic" \
        "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorMic" \
        "DisplayIcon" "$INSTDIR\${APP_EXE}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorMic" \
        "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
SectionEnd

Section "Uninstall"
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "MonitorMic"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorMic"
    Delete "$INSTDIR\MonitorMic.exe"
    Delete "$INSTDIR\Uninstall.exe"
    Delete "$SMPROGRAMS\MonitorMic.lnk"
    Delete "$DESKTOP\MonitorMic.lnk"
    RMDir "$INSTDIR"
SectionEnd
