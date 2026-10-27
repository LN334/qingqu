Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"

!define APP_NAME "清渠"
!define APP_FULL "清渠 · 村镇水处理站智能运维平台"
!define APP_VERSION "2.0.7"
!define APP_PUBLISHER "LN334"
!define APP_EXE "Qingqu.exe"
!define REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\QingquNative"
!define LEGACY_REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ZhifuWaterNative"

Name "${APP_FULL}"
OutFile "Qingqu-Setup-${APP_VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
InstallDirRegKey HKLM "${REG_KEY}" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APP_FULL} ${APP_VERSION}"

!define MUI_ICON "..\..\desktop\build\icon.ico"
!define MUI_UNICON "..\..\desktop\build\icon.ico"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Section "主程序" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"
  File /r "..\publish\*.*"

  ; 旧版本（智赋乡水 / ZhifuWater.exe）迁移清理，避免桌面出现两个图标、程序与卸载项并存
  Delete "$DESKTOP\智赋乡水.lnk"
  Delete "$DESKTOP\智赋乡水 · 智慧水处理平台.lnk"
  Delete "$DESKTOP\智赋乡水·智慧水处理平台.lnk"
  Delete "$SMPROGRAMS\智赋乡水\智赋乡水.lnk"
  Delete "$SMPROGRAMS\智赋乡水\卸载 智赋乡水.lnk"
  RMDir "$SMPROGRAMS\智赋乡水"
  DeleteRegKey HKLM "${LEGACY_REG_KEY}"
  IfFileExists "$PROGRAMFILES64\智赋乡水\ZhifuWater.exe" 0 +2
    RMDir /r "$PROGRAMFILES64\智赋乡水"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKLM "${REG_KEY}" "DisplayName" "${APP_FULL}"
  WriteRegStr HKLM "${REG_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "${REG_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKLM "${REG_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKLM "${REG_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${REG_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "${REG_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${REG_KEY}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${REG_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKLM "${REG_KEY}"
  DeleteRegKey HKLM "${LEGACY_REG_KEY}"
SectionEnd
