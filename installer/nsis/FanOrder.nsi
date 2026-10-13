Unicode true
RequestExecutionLevel admin
ManifestDPIAware true
SetCompressor /SOLID zlib
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
Name "$(String0)"
OutFile "${OUTPUT}"
Icon "${ICON}"
InstallDir "$PROGRAMFILES64\tianqistudio\V12UltraController"
BrandingText "tianqistudio · tianqistudio.com"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "风序 · FanOrder"
VIAddVersionKey /LANG=1033 "ProductName" "FanOrder"
VIAddVersionKey /LANG=2052 "CompanyName" "tianqistudio"
VIAddVersionKey /LANG=1033 "CompanyName" "tianqistudio"
VIAddVersionKey /LANG=2052 "FileDescription" "FanOrder 安装程序"
VIAddVersionKey /LANG=1033 "FileDescription" "FanOrder Installer"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "tianqistudio"
VIAddVersionKey /LANG=1033 "LegalCopyright" "tianqistudio"
ShowInstDetails show
ShowUninstDetails show
Var DesktopChoice
Var MenuChoice
Var DesktopBox
Var MenuBox
!define MUI_LANGDLL_REGISTRY_ROOT HKLM
!define MUI_LANGDLL_REGISTRY_KEY "Software\tianqistudio\V12UltraController\Installer"
!define MUI_LANGDLL_REGISTRY_VALUENAME "InstallerLanguage"
!define MUI_LANGDLL_ALWAYSSHOW
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "$(String2)"
!insertmacro MUI_PAGE_WELCOME
Page custom OptionsPage OptionsLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$(String3)"
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "$(String4)"
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"
LangString String0 ${LANG_SIMPCHINESE} "风序 · FanOrder"
LangString String0 ${LANG_ENGLISH} "FanOrder"
LangString String1 ${LANG_SIMPCHINESE} "FanOrder 安装程序"
LangString String1 ${LANG_ENGLISH} "FanOrder Installer"
LangString String2 ${LANG_SIMPCHINESE} "安装风序 · FanOrder ${VERSION}。$\r$\n$\r$\n请先从托盘退出风序。安装会保留已有配置、用户登记和启动偏好。$\r$\n$\r$\n首次安装需要官方 PawnIO 2.2.0。安装器不包含驱动，安装后请打开设置完成设备接管。"
LangString String2 ${LANG_ENGLISH} "Install FanOrder ${VERSION}.$\r$\n$\r$\nExit FanOrder from the tray first. Existing configuration, user registration and startup preferences are retained.$\r$\n$\r$\nOfficial PawnIO 2.2.0 is required separately. No driver is bundled. Open Settings after installation to commission the device."
LangString String3 ${LANG_SIMPCHINESE} "风序 · FanOrder 已安装。$\r$\n$\r$\n可从开始菜单或安装目录打开设置。登录托盘启动可在设置中开启。$\r$\n$\r$\n已有配置和启动偏好保持不变。"
LangString String3 ${LANG_ENGLISH} "FanOrder is installed.$\r$\n$\r$\nOpen Settings from the Start menu or install folder. Choose Chinese or English under General. Login tray startup is optional.$\r$\n$\r$\nExisting configuration and startup preferences are retained."
LangString String4 ${LANG_SIMPCHINESE} "卸载前请退出风序托盘。$\r$\n$\r$\n卸载会由后台撤回设备接管并恢复接管前的任务状态，不启动 Myth.Cool。恢复失败将停止卸载。$\r$\n$\r$\n配置、审计、用户登记和恢复备份将保留。"
LangString String4 ${LANG_ENGLISH} "Exit FanOrder from the tray before uninstalling.$\r$\n$\r$\nThe backend releases device ownership and restores the previous task state without starting Myth.Cool. Failed recovery stops uninstallation.$\r$\n$\r$\nConfiguration, audit records, user registration and recovery backups are retained."
LangString String5 ${LANG_SIMPCHINESE} "仅支持 Windows x64。"
LangString String5 ${LANG_ENGLISH} "Windows x64 is required."
LangString String6 ${LANG_SIMPCHINESE} "此版本需要交互安装，不支持静默模式。"
LangString String6 ${LANG_ENGLISH} "This version requires interactive installation; silent mode is not supported."
LangString String7 ${LANG_SIMPCHINESE} "快捷方式"
LangString String7 ${LANG_ENGLISH} "Shortcuts"
LangString String8 ${LANG_SIMPCHINESE} "选择需要的入口，升级时沿用已保存的选择。"
LangString String8 ${LANG_ENGLISH} "Choose your shortcuts. Upgrades retain saved choices."
LangString String9 ${LANG_SIMPCHINESE} "创建桌面快捷方式"
LangString String9 ${LANG_ENGLISH} "Create a desktop shortcut"
LangString String10 ${LANG_SIMPCHINESE} "创建开始菜单快捷方式"
LangString String10 ${LANG_ENGLISH} "Create a Start menu shortcut"
LangString String11 ${LANG_SIMPCHINESE} "开始菜单分组：tianqistudio$\r$\n安装目录：$INSTDIR"
LangString String11 ${LANG_ENGLISH} "Start menu group: tianqistudio$\r$\nInstall folder: $INSTDIR"
LangString String12 ${LANG_SIMPCHINESE} "安装"
LangString String12 ${LANG_ENGLISH} "Install"
LangString String13 ${LANG_SIMPCHINESE} "安装未完成，请查看上方详细信息。原有配置和恢复资料已保留。"
LangString String13 ${LANG_ENGLISH} "Installation did not finish. See the details above. Existing configuration and recovery data are retained."
LangString String14 ${LANG_SIMPCHINESE} "需要本人确认恢复接管，不支持静默卸载。"
LangString String14 ${LANG_ENGLISH} "Ownership recovery requires your confirmation; silent uninstallation is not supported."
LangString String15 ${LANG_SIMPCHINESE} "卸载未完成，请查看上方详细信息。请修复后重试，恢复资料已保留。"
LangString String15 ${LANG_ENGLISH} "Uninstallation did not finish. See the details above, resolve the issue and retry. Recovery data are retained."


Function .onInit
  SetRegView 64
  !insertmacro MUI_LANGDLL_DISPLAY
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "$(String5)"
    Abort
  ${EndIf}
  IfSilent 0 +3
    MessageBox MB_ICONSTOP "$(String6)"
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
  !insertmacro MUI_HEADER_TEXT "$(String7)" "$(String8)"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateCheckbox} 0 16u 100% 16u "$(String9)"
  Pop $DesktopBox
  ${NSD_SetState} $DesktopBox $DesktopChoice
  ${NSD_CreateCheckbox} 0 44u 100% 16u "$(String10)"
  Pop $MenuBox
  ${NSD_SetState} $MenuBox $MenuChoice
  ${NSD_CreateLabel} 0 82u 100% 42u "$(String11)"
  Pop $0
  nsDialogs::Show
FunctionEnd
Function OptionsLeave
  ${NSD_GetState} $DesktopBox $DesktopChoice
  ${NSD_GetState} $MenuBox $MenuChoice
FunctionEnd
Section "$(String12)"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR\payload"
  File /r "${PAYLOAD}\*"
  WriteUninstaller "$PLUGINSDIR\payload\Uninstall.exe"
  ${DisableX64FSRedirection}
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\payload\Maintenance.ps1" -Mode Install -Language $LANGUAGE -Payload "$PLUGINSDIR\payload" -Desktop $DesktopChoice -StartMenu $MenuChoice'
  Pop $0
  ${EnableX64FSRedirection}
  ${If} $0 != 0
    SetErrorLevel 1
    MessageBox MB_ICONSTOP "$(String13)"
    Abort
  ${EndIf}
SectionEnd
Function un.onInit
  SetRegView 64
  !insertmacro MUI_UNGETLANGUAGE
  IfSilent 0 +3
    MessageBox MB_ICONSTOP "$(String14)"
    Abort
  StrCpy $INSTDIR "$PROGRAMFILES64\tianqistudio\V12UltraController"
FunctionEnd
Section "Uninstall"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File "${PAYLOAD}\Maintenance.ps1"
  ${DisableX64FSRedirection}
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Maintenance.ps1" -Mode Uninstall -Language $LANGUAGE'
  Pop $0
  ${EnableX64FSRedirection}
  ${If} $0 != 0
    SetErrorLevel 1
    MessageBox MB_ICONSTOP "$(String15)"
    Abort
  ${EndIf}
  Delete "$INSTDIR\Uninstall.exe"
SectionEnd
