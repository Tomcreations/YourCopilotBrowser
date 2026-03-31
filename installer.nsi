!include "MUI2.nsh"
!include "x64.nsh"
!include "nsDialogs.nsh"
!include "WinMessages.nsh"

!define INSTALLER_VERSION "1.0.0"

Name "YCB Browser"
OutFile "YCB-Setup.exe"
InstallDir "$PROGRAMFILES64\YCB"
RequestExecutionLevel admin
CRCCheck off

!define MUI_ICON "icon.ico"
!define MUI_UNICON "icon.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\YCB.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch YCB Browser"
!define MUI_FINISHPAGE_RUN_CHECKED

Var EXISTING_INSTALL
Var ACTION_OPTION
Var AI_OPTION
Var REMOVE_APPDATA
Var Dialog
Var RadioInstall
Var RadioReinstall
Var RadioUpdate
Var RadioUninstall
Var RadioSettings
Var ChkRemoveAppData
Var ChkAI
Var ChkAISettings

Page custom MenuPageCreate MenuPageLeave
Page custom SettingsPageCreate SettingsPageLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
    StrCpy $ACTION_OPTION "install"
    StrCpy $REMOVE_APPDATA "0"
    StrCpy $AI_OPTION "on"
    StrCpy $EXISTING_INSTALL "0"
    SetRegView 64
    ReadRegStr $0 HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\YCB" "InstallLocation"
    StrCmp $0 "" tryAltKey onInitExisting
tryAltKey:
    ReadRegStr $0 HKLM "Software\YCB" "InstallLocation"
    StrCmp $0 "" onInitDone onInitExisting
onInitExisting:
    StrCpy $EXISTING_INSTALL "1"
    StrCpy $INSTDIR $0
onInitDone:
FunctionEnd

Function MenuPageCreate
    nsDialogs::Create 1018
    Pop $Dialog

    GetDlgItem $0 $HWNDPARENT 1
    SendMessage $0 ${WM_SETTEXT} 0 "STR:Select"

    ${NSD_CreateLabel} 0 0 100% 14u "YCB Browser — Installer"
    Pop $0

    StrCmp $EXISTING_INSTALL "1" existingMenu freshMenu

existingMenu:
    ${NSD_CreateLabel} 0 18u 100% 10u "Existing installation detected. Choose an action:"
    Pop $0
    ${NSD_CreateRadioButton} 8u 32u 100% 12u "Uninstall"
    Pop $RadioUninstall
    ${NSD_CreateLabel} 20u 46u 100% 18u "Removes YCB Browser. WARNING: can also delete all user data (bookmarks, passwords, settings)."
    Pop $0
    ${NSD_CreateRadioButton} 8u 68u 100% 12u "Reinstall"
    Pop $RadioReinstall
    ${NSD_CreateLabel} 20u 82u 100% 12u "Removes and reinstalls YCB. Option to clear AppData below."
    Pop $0
    ${NSD_CreateRadioButton} 8u 98u 100% 12u "Update"
    Pop $RadioUpdate
    ${NSD_CreateLabel} 20u 112u 100% 12u "Installs latest version and keeps your AppData. (Recommended)"
    Pop $0
    ${NSD_CreateRadioButton} 8u 128u 100% 12u "Master Settings"
    Pop $RadioSettings
    ${NSD_CreateLabel} 20u 142u 100% 12u "Toggle AI and other options for your existing install without reinstalling."
    Pop $0
    ${NSD_CreateCheckbox} 8u 162u 100% 12u "Also remove AppData (WARNING: permanently deletes bookmarks, passwords, settings)"
    Pop $ChkRemoveAppData
    ${NSD_Check} $RadioUpdate
    Goto menuDone

freshMenu:
    ${NSD_CreateLabel} 0 18u 100% 10u "No existing installation found. Ready to install:"
    Pop $0
    ${NSD_CreateRadioButton} 8u 32u 100% 12u "Install YCB Browser"
    Pop $RadioInstall
    ${NSD_CreateLabel} 20u 46u 100% 12u "Install YCB Browser to your computer."
    Pop $0
    ${NSD_CreateCheckbox} 8u 70u 100% 12u "Enable built-in AI (GitHub Copilot) integration"
    Pop $ChkAI
    ${NSD_Check} $ChkAI
    ${NSD_Check} $RadioInstall

menuDone:
    nsDialogs::Show
FunctionEnd

Function MenuPageLeave
    StrCmp $EXISTING_INSTALL "1" readExisting readFresh

readExisting:
    ${NSD_GetState} $RadioUninstall $0
    StrCmp $0 1 setUninstall checkReinstall
checkReinstall:
    ${NSD_GetState} $RadioReinstall $0
    StrCmp $0 1 setReinstall checkUpdate
checkUpdate:
    ${NSD_GetState} $RadioUpdate $0
    StrCmp $0 1 setUpdate checkSettings
checkSettings:
    ${NSD_GetState} $RadioSettings $0
    StrCmp $0 1 setSettings readRemoveAppData
setUninstall:
    StrCpy $ACTION_OPTION "uninstall"
    Goto readRemoveAppData
setReinstall:
    StrCpy $ACTION_OPTION "reinstall"
    Goto readRemoveAppData
setUpdate:
    StrCpy $ACTION_OPTION "update"
    MessageBox MB_YESNO "Before updating, make sure this is the latest installer.$\r$\nContinue with update?" IDYES readRemoveAppData IDNO abortUpdate
abortUpdate:
    Abort
setSettings:
    StrCpy $ACTION_OPTION "settings"
readRemoveAppData:
    ${NSD_GetState} $ChkRemoveAppData $0
    StrCmp $0 1 setRemoveFlag skipRemoveFlag
setRemoveFlag:
    StrCpy $REMOVE_APPDATA "1"
skipRemoveFlag:
    ; Settings handled on the next page — skip checkUninstall for settings
    StrCmp $ACTION_OPTION "settings" leaveDone checkUninstallNow

checkUninstallNow:
    ; Handle uninstall — do it now and quit before InstFiles page
    StrCmp $ACTION_OPTION "uninstall" 0 leaveDone
    nsExec::Exec 'taskkill /F /IM YCB.exe'
    nsExec::Exec 'taskkill /F /IM ycb-smartdl.exe'
    Sleep 500
    RMDir /r "$INSTDIR"
    Delete "$SMPROGRAMS\YCB\*.*"
    RMDir "$SMPROGRAMS\YCB"
    Delete "$DESKTOP\YCB App.lnk"
    StrCmp $REMOVE_APPDATA "1" doRmData skipRmData
doRmData:
    RMDir /r "$APPDATA\YCB-Browser"
skipRmData:
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\YCB"
    DeleteRegKey HKLM "Software\YCB"
    MessageBox MB_OK "YCB has been uninstalled."
    Quit

readFresh:
    StrCpy $ACTION_OPTION "install"
    ${NSD_GetState} $ChkAI $0
    StrCmp $0 1 setAIOn setAIOff
setAIOn:
    StrCpy $AI_OPTION "on"
    Goto leaveDone
setAIOff:
    StrCpy $AI_OPTION "off"

leaveDone:
FunctionEnd


Function SettingsPageCreate
    ; Only show this page when action = settings
    StrCmp $ACTION_OPTION "settings" 0 skipSettings

    nsDialogs::Create 1018
    Pop $Dialog

    GetDlgItem $0 $HWNDPARENT 1
    SendMessage $0 ${WM_SETTEXT} 0 "STR:Save"

    ${NSD_CreateLabel} 0 0 100% 14u "YCB Browser — Master Settings"
    Pop $0
    ${NSD_CreateLabel} 0 18u 100% 10u "Adjust settings for your existing installation:"
    Pop $0

    ; AI integration checkbox — pre-check based on current registry value
    ReadRegStr $1 HKLM "Software\YCB" "AIOption"
    ${NSD_CreateCheckbox} 8u 38u 100% 14u "Enable built-in AI (GitHub Copilot) integration"
    Pop $ChkAISettings
    StrCmp $1 "off" settingsPageDone checkAIOn
checkAIOn:
    ${NSD_Check} $ChkAISettings

settingsPageDone:
    ; Auto-save when Back is clicked — handled by SettingsPageLeave not being called on Back
    nsDialogs::Show
    Return

skipSettings:
    Abort
FunctionEnd

Function SettingsPageLeave
    ; Save settings and quit (Next/Save clicked)
    ${NSD_GetState} $ChkAISettings $0
    StrCmp $0 1 saveOn saveOff
saveOn:
    WriteRegStr HKLM "Software\YCB" "AIOption" "on"
    Goto saved
saveOff:
    WriteRegStr HKLM "Software\YCB" "AIOption" "off"
saved:
    MessageBox MB_OK "Settings saved."
    Quit
FunctionEnd

Section "Install"
    ${If} ${RunningX64}
        ${DisableX64FSRedirection}
        SetRegView 64
    ${EndIf}

    nsExec::Exec 'taskkill /F /IM YCB.exe'
    nsExec::Exec 'taskkill /F /IM ycb-smartdl.exe'
    Sleep 1000
    ; Only wipe files on reinstall — update and install leave existing files/AppData alone
    ; AppData is NEVER touched during reinstall or install — only uninstall can remove it
    StrCmp $ACTION_OPTION "reinstall" removeOldFiles skipRemoveFiles
removeOldFiles:
    RMDir /r "$INSTDIR"
skipRemoveFiles:
    SetOutPath "$INSTDIR"
    CreateDirectory "$INSTDIR"
    ReadRegStr $0 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
    StrCmp $0 "" 0 webview2Ok
    ReadRegStr $0 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
    StrCmp $0 "" 0 webview2Ok
    DetailPrint "Installing WebView2 Runtime..."
    SetOutPath "$TEMP"
    File "MicrosoftEdgeWebview2Setup.exe"
    nsExec::ExecToLog '"$TEMP\MicrosoftEdgeWebview2Setup.exe" /silent /install'
    Delete "$TEMP\MicrosoftEdgeWebview2Setup.exe"
webview2Ok:
    SetOutPath "$INSTDIR"
    DetailPrint "Installing YCB Browser..."
    File /r "publish\*"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    CreateDirectory "$SMPROGRAMS\YCB"
    CreateShortcut "$SMPROGRAMS\YCB\YCB App.lnk" "$INSTDIR\YCB.exe"
    CreateShortcut "$SMPROGRAMS\YCB\Uninstall YCB.lnk" "$INSTDIR\Uninstall.exe"
    Delete "$DESKTOP\YCB App.lnk"
    CreateShortcut "$DESKTOP\YCB App.lnk" "$INSTDIR\YCB.exe"
    WriteRegStr HKLM "Software\YCB" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "Software\YCB" "AIOption" "$AI_OPTION"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\YCB" "DisplayName" "YCB Browser"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\YCB" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\YCB" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\YCB" "Publisher" "YCB"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\YCB" "DisplayVersion" "1.0.0"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\YCB" "DisplayIcon" "$INSTDIR\YCB.exe"
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\YCB" "NoModify" 1
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\YCB" "NoRepair" 1
    ${If} ${RunningX64}
        ${EnableX64FSRedirection}
    ${EndIf}
SectionEnd

Section "Uninstall"
    ${If} ${RunningX64}
        ${DisableX64FSRedirection}
        SetRegView 64
    ${EndIf}
    ReadRegStr $0 HKLM "Software\YCB" "RemoveAppDataOnAction"
    StrCmp $0 "1" unRmData skipUnRmData
unRmData:
    RMDir /r "$APPDATA\YCB-Browser"
skipUnRmData:
    RMDir /r "$INSTDIR"
    Delete "$SMPROGRAMS\YCB\*.*"
    RMDir "$SMPROGRAMS\YCB"
    Delete "$DESKTOP\YCB App.lnk"
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\YCB"
    DeleteRegKey HKLM "Software\YCB"
    ${If} ${RunningX64}
        ${EnableX64FSRedirection}
    ${EndIf}
SectionEnd
