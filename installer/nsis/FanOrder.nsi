Unicode true
RequestExecutionLevel admin
ManifestDPIAware true
SetCompressor /SOLID zlib
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
Name "风序 · FanOrder"
OutFile "${OUTPUT}"
Icon "${ICON}"
InstallDir "$PROGRAMFILES64\tianqistudio\V12UltraController"
BrandingText "tianqistudio · tianqistudio.com"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "风序 · FanOrder"
VIAddVersionKey /LANG=2052 "CompanyName" "tianqistudio"
VIAddVersionKey /LANG=2052 "FileDescription" "FanOrder 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "tianqistudio"
ShowInstDetails show
ShowUninstDetails show
Var DesktopChoice
Var MenuChoice
Var DesktopBox
Var MenuBox
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "安装风序 · FanOrder ${VERSION}。$\r$\n$\r$\n请先从托盘退出风序。安装会保留已有配置、用户登记和启动偏好。$\r$\n$\r$\n首次安装需要官方 PawnIO 2.2.0。安装器不包含驱动，安装后请打开设置完成设备接管。"
!insertmacro MUI_PAGE_WELCOME
Page custom OptionsPage OptionsLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "风序 · FanOrder 已安装。$\r$\n$\r$\n可从开始菜单或安装目录打开设置。登录托盘启动可在设置中开启。$\r$\n$\r$\n已有配置和启动偏好保持不变。"
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "卸载前请退出风序托盘。$\r$\n$\r$\n卸载会由后台撤回设备接管并恢复接管前的任务状态，不启动 Myth.Cool。恢复失败将停止卸载。$\r$\n$\r$\n配置、审计、用户登记和恢复备份将保留。"
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "仅支持 Windows x64。"
    Abort
  ${EndIf}
  IfSilent 0 +3
    MessageBox MB_ICONSTOP "此版本需要交互安装，不支持静默模式。"
    Abort
  SetRegView 64
  StrCpy $INSTDIR "$PROGRAMFILES64\tianqistudio\V12UltraController"
  StrCpy $DesktopChoice 1
  StrCpy $MenuChoice 1
  ClearErrors
  ReadRegDWORD $0 HKLM "Software\TianqiStudio\V12UltraController\Installer" "DesktopShortcut"
  ${IfNot} ${Errors}
    ${If} $0 == 0
    ${OrIf} $0 == 1
      StrCpy $DesktopChoice $0
    ${EndIf}
  ${EndIf}
  ClearErrors
  ReadRegDWORD $0 HKLM "Software\TianqiStudio\V12UltraController\Installer" "StartMenuShortcut"
  ${IfNot} ${Errors}
    ${If} $0 == 0
    ${OrIf} $0 == 1
      StrCpy $MenuChoice $0
    ${EndIf}
  ${EndIf}
FunctionEnd
Function OptionsPage
  !insertmacro MUI_HEADER_TEXT "快捷方式" "选择需要的入口，升级时沿用已保存的选择。"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateCheckbox} 0 16u 100% 16u "创建桌面快捷方式"
  Pop $DesktopBox
  ${NSD_SetState} $DesktopBox $DesktopChoice
  ${NSD_CreateCheckbox} 0 44u 100% 16u "创建开始菜单快捷方式"
  Pop $MenuBox
  ${NSD_SetState} $MenuBox $MenuChoice
  ${NSD_CreateLabel} 0 82u 100% 42u "开始菜单分组：tianqistudio$\r$\n安装目录：$INSTDIR"
  Pop $0
  nsDialogs::Show
FunctionEnd
Function OptionsLeave
  ${NSD_GetState} $DesktopBox $DesktopChoice
  ${NSD_GetState} $MenuBox $MenuChoice
FunctionEnd
Section "安装"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR\payload"
  File /r "${PAYLOAD}\*"
  WriteUninstaller "$PLUGINSDIR\payload\Uninstall.exe"
  ${DisableX64FSRedirection}
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\payload\Maintenance.ps1" -Mode Install -Payload "$PLUGINSDIR\payload" -Desktop $DesktopChoice -StartMenu $MenuChoice'
  Pop $0
  ${EnableX64FSRedirection}
  ${If} $0 != 0
    SetErrorLevel 1
    MessageBox MB_ICONSTOP "安装未完成，请查看上方详细信息。原有配置和恢复资料已保留。"
    Abort
  ${EndIf}
SectionEnd
Function un.onInit
  IfSilent 0 +3
    MessageBox MB_ICONSTOP "需要本人确认恢复接管，不支持静默卸载。"
    Abort
  StrCpy $INSTDIR "$PROGRAMFILES64\tianqistudio\V12UltraController"
FunctionEnd
Section "Uninstall"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File "${PAYLOAD}\Maintenance.ps1"
  ${DisableX64FSRedirection}
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Maintenance.ps1" -Mode Uninstall'
  Pop $0
  ${EnableX64FSRedirection}
  ${If} $0 != 0
    SetErrorLevel 1
    MessageBox MB_ICONSTOP "卸载未完成，请查看上方详细信息。请修复后重试，恢复资料已保留。"
    Abort
  ${EndIf}
  Delete "$INSTDIR\Uninstall.exe"
SectionEnd
