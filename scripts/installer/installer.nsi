; ============================================================================
; AIMemory NSIS Installer (post-Tauri)
;
; Bundles AIMemory.Api, AIMemory.Ingestor, AIMemory.Mcp (framework-dependent
; single-file publishes) + the React SPA built into wwwroot. Registers the
; .NET services with sc.exe and drops a browser shortcut to the local API.
;
; Setup-type page offers Full (primary) vs Ingestor-only (secondary). The
; ingestor-only path skips the API binaries and the API service registration,
; drops a shortcut to the headless pairing PowerShell script instead.
;
; Build:  scripts/installer/build-installer.ps1 wraps publish + makensis.
; Inputs (set on the makensis command line via /D):
;   /DVERSION="0.2.0"
;   /DSTAGE_DIR="C:\path\to\scripts\installer\staging"
;   /DOUTPUT_FILE="C:\path\to\AIMemory_0.2.0_x64-setup.exe"
; ============================================================================

!ifndef VERSION
  !define VERSION "0.2.0"
!endif

!ifndef STAGE_DIR
  !error "STAGE_DIR must be set via /D — run build-installer.ps1 instead of makensis directly."
!endif

!ifndef OUTPUT_FILE
  !define OUTPUT_FILE "AIMemory_${VERSION}_x64-setup.exe"
!endif

Name "AIMemory ${VERSION}"
OutFile "${OUTPUT_FILE}"
Unicode true
InstallDir "$PROGRAMFILES64\AIMemory"
InstallDirRegKey HKLM "Software\AIMemory" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

; ----------------------------- UI / pages -----------------------------------

!define MUI_ABORTWARNING

; Page order: welcome → EULA → setup-type (mode) → install dir → install → finish
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${STAGE_DIR}\license.txt"
Page custom SetupTypePage SetupTypePageLeave
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ----------------------------- variables ------------------------------------

Var InstallMode          ; "full" or "ingestor-only"
Var SetupTypeHwnd
Var RadioFullHwnd
Var RadioIngestorHwnd

; ----------------------------- Setup type custom page -----------------------

Function SetupTypePage
  !insertmacro MUI_HEADER_TEXT "Setup Type" "Choose how AIMemory should be installed on this machine."

  nsDialogs::Create 1018
  Pop $SetupTypeHwnd
  ${If} $SetupTypeHwnd == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 24u "Pick a deployment shape. Most users want Full. Pick Ingestor-only if this machine should forward index events to another AIMemory server on the LAN."

  ${NSD_CreateRadioButton} 10 30u 100% 12u "&Full install (default) — API, Ingestor, MCP server, web UI"
  Pop $RadioFullHwnd
  ${NSD_CreateLabel} 30 44u 90% 12u "Recommended for single-machine setups and primaries in distributed mode."

  ${NSD_CreateRadioButton} 10 64u 100% 12u "&Ingestor-only — forward events to a remote primary"
  Pop $RadioIngestorHwnd
  ${NSD_CreateLabel} 30 78u 90% 12u "Installs only the ingestor service plus a headless pairing wizard."

  ; Default selection
  ${If} $InstallMode == "ingestor-only"
    ${NSD_SetState} $RadioIngestorHwnd ${BST_CHECKED}
  ${Else}
    ${NSD_SetState} $RadioFullHwnd ${BST_CHECKED}
  ${EndIf}

  nsDialogs::Show
FunctionEnd

Function SetupTypePageLeave
  ${NSD_GetState} $RadioIngestorHwnd $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $InstallMode "ingestor-only"
  ${Else}
    StrCpy $InstallMode "full"
  ${EndIf}
FunctionEnd

; ----------------------------- .NET prereq check ---------------------------

Function .onInit
  StrCpy $InstallMode "full"

  ; Probe for .NET 10. dotnet --list-runtimes is the canonical way.
  nsExec::ExecToStack 'dotnet --list-runtimes'
  Pop $0   ; exit code
  Pop $1   ; output
  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION|MB_YESNO ".NET 10 runtime is required and could not be detected.$\r$\n$\r$\nOpen the .NET download page now?" IDYES openDotnetPage IDNO continueAnyway
    openDotnetPage:
      ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/10.0"
      Abort
    continueAnyway:
      Return
  ${EndIf}

  ${StrLoc} $2 $1 "Microsoft.NETCore.App 10." ">"
  ${If} $2 == ""
    MessageBox MB_ICONEXCLAMATION|MB_YESNO ".NET 10 runtime is required and was not found.$\r$\n$\r$\nOpen the .NET download page now?" IDYES openDotnetPage2 IDNO continueAnyway2
    openDotnetPage2:
      ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/10.0"
      Abort
    continueAnyway2:
      Return
  ${EndIf}
FunctionEnd

; ----------------------------- Install sections ----------------------------

Section "-Common files" SecCommon
  SetOutPath "$INSTDIR"

  ; Shared deps (libgit2, sqlite) live alongside the executables in the publish output.
  ; Common scripts:
  File "${STAGE_DIR}\license.txt"
  File "${STAGE_DIR}\README.txt"

  ; Drop the headless pairing script regardless of mode — full installs can also re-pair.
  SetOutPath "$INSTDIR\scripts"
  File "${STAGE_DIR}\scripts\Pair-AIMemoryIngestor.ps1"

  ; Persist install mode + version
  WriteRegStr HKLM "Software\AIMemory" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "Software\AIMemory" "InstallMode" "$InstallMode"
  WriteRegStr HKLM "Software\AIMemory" "Version" "${VERSION}"

  ; Uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIMemory" "DisplayName" "AIMemory ${VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIMemory" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIMemory" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIMemory" "Publisher" "AIMemory"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIMemory" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIMemory" "NoModify" "1"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIMemory" "NoRepair" "1"
SectionEnd

Section "-Ingestor" SecIngestor
  SetOutPath "$INSTDIR\Ingestor"
  File /r "${STAGE_DIR}\Ingestor\*.*"

  ; Register the service. Use sc.exe; binPath needs quoting because of spaces in $INSTDIR.
  nsExec::ExecToLog 'sc.exe stop "aimemory-ingestor"'   ; best-effort, ignored on first install
  nsExec::ExecToLog 'sc.exe delete "aimemory-ingestor"'

  ; binPath= requires a trailing space after the equals; that's the sc.exe quirk.
  nsExec::ExecToLog 'sc.exe create "aimemory-ingestor" binPath= "\"$INSTDIR\Ingestor\AIMemory.Ingestor.exe\"" start= auto DisplayName= "AIMemory Ingestor"'
  nsExec::ExecToLog 'sc.exe description "aimemory-ingestor" "Indexes code repositories and forwards events to the AIMemory API."'
SectionEnd

Section "-Full mode only" SecFull
  ${If} $InstallMode != "full"
    Goto skipFull
  ${EndIf}

  ; API service binaries — wwwroot/ is included automatically because the
  ; AIMemory.Api.csproj's PublishRunWebpack target builds the SPA into
  ; wwwroot/ during publish.
  SetOutPath "$INSTDIR\Api"
  File /r "${STAGE_DIR}\Api\*.*"

  ; MCP server (used by Claude Code / Codex CLI to talk to the local API)
  SetOutPath "$INSTDIR\Mcp"
  File /r "${STAGE_DIR}\Mcp\*.*"

  ; Register the API service
  nsExec::ExecToLog 'sc.exe stop "aimemory-api"'
  nsExec::ExecToLog 'sc.exe delete "aimemory-api"'
  nsExec::ExecToLog 'sc.exe create "aimemory-api" binPath= "\"$INSTDIR\Api\AIMemory.Api.exe\"" start= auto DisplayName= "AIMemory API"'
  nsExec::ExecToLog 'sc.exe description "aimemory-api" "ASP.NET Core API + web UI for AIMemory."'

  ; Start both services
  nsExec::ExecToLog 'sc.exe start "aimemory-api"'
  nsExec::ExecToLog 'sc.exe start "aimemory-ingestor"'

  ; Start menu shortcut — points the user's default browser at the local UI.
  ; We can't know the port at install time (the API picks one on first start
  ; and writes %ProgramData%\AIMemory\Api\port); we ship a launcher script
  ; that reads it and opens the right URL.
  SetOutPath "$INSTDIR"
  File "${STAGE_DIR}\Open-AIMemory.cmd"
  CreateDirectory "$SMPROGRAMS\AIMemory"
  CreateShortcut "$SMPROGRAMS\AIMemory\AIMemory.lnk" "$INSTDIR\Open-AIMemory.cmd" "" "$INSTDIR\Api\AIMemory.Api.exe" 0

  skipFull:
SectionEnd

Section "-Ingestor-only mode" SecIngestorOnly
  ${If} $InstallMode != "ingestor-only"
    Goto skipIngestorOnly
  ${EndIf}

  ; Pairing shortcut
  CreateDirectory "$SMPROGRAMS\AIMemory"
  CreateShortcut "$SMPROGRAMS\AIMemory\Pair Ingestor.lnk" "powershell.exe" "-ExecutionPolicy Bypass -File \"$INSTDIR\scripts\Pair-AIMemoryIngestor.ps1\"" "$INSTDIR\Ingestor\AIMemory.Ingestor.exe" 0

  ; Do NOT auto-start the ingestor on ingestor-only installs — config is empty,
  ; the service would just fail. The pairing script starts it on success.

  skipIngestorOnly:
SectionEnd

; ----------------------------- Uninstall ----------------------------------

Section "Uninstall"
  ; Stop + remove services (best-effort)
  nsExec::ExecToLog 'sc.exe stop "aimemory-ingestor"'
  nsExec::ExecToLog 'sc.exe stop "aimemory-api"'
  nsExec::ExecToLog 'sc.exe delete "aimemory-ingestor"'
  nsExec::ExecToLog 'sc.exe delete "aimemory-api"'

  ; Remove files
  RMDir /r "$INSTDIR\Api"
  RMDir /r "$INSTDIR\Ingestor"
  RMDir /r "$INSTDIR\Mcp"
  RMDir /r "$INSTDIR\scripts"
  Delete "$INSTDIR\Open-AIMemory.cmd"
  Delete "$INSTDIR\license.txt"
  Delete "$INSTDIR\README.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  ; Start menu
  Delete "$SMPROGRAMS\AIMemory\AIMemory.lnk"
  Delete "$SMPROGRAMS\AIMemory\Pair Ingestor.lnk"
  RMDir "$SMPROGRAMS\AIMemory"

  ; Registry
  DeleteRegKey HKLM "Software\AIMemory"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIMemory"

  ; ProgramData is intentionally preserved on uninstall so an upgrade reinstall
  ; keeps the database, install salt, port file, and TLS cert. Users who want
  ; to wipe state should delete %ProgramData%\AIMemory manually.
SectionEnd
