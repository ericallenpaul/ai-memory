; AIMemory Desktop -- NSIS installer hooks.
;
; Tauri 2.x exposes only four lifecycle macros (PRE/POSTINSTALL +
; PRE/POSTUNINSTALL). It does NOT expose a way to inject MUI pages directly,
; so we declare a custom nsDialogs page at the top of this file. Because Tauri
; `!include`s this file BEFORE its own `!insertmacro MUI_PAGE_*` calls, our
; "Setup Type" page registers as the first wizard page (ahead of Welcome).
; That's an acceptable UX trade-off; the alternative is forking the entire
; Tauri NSI template via `bundle.windows.nsis.template`, which is heavier.
;
; Phase 10 adds component selection (Full vs Ingestor-only):
;   * Full          -> ships everything, registers aimemory-api +
;                      aimemory-ingestor, writes app-mode.json {mode:"full"}.
;   * Ingestor-only -> registers only aimemory-ingestor, deletes the API
;                      binaries from $INSTDIR (cleaner uninstall + clearer
;                      troubleshooting), writes app-mode.json
;                      {mode:"ingestor-only"}, replaces the Start Menu
;                      shortcut with "AIMemory Ingestor Setup".
; The Tauri shell binary itself is unchanged; phase 9 picks the UI face based
; on the app-mode.json marker file.

!include LogicLib.nsh
!include nsDialogs.nsh

;-------------------------------------------------------------------------
; Globals
;-------------------------------------------------------------------------
; "full" or "ingestor-only". Set on the custom page leave handler; consumed by
; both POSTINSTALL (service registration, file pruning, app-mode.json) and the
; uninstaller via the registry value we persist at install time.
Var AIMemoryInstallMode

; nsDialogs control handles for the Setup Type page.
Var SetupTypeDialog
Var SetupTypeRadioFull
Var SetupTypeRadioIngestor
Var SetupTypeLabelFullDesc
Var SetupTypeLabelIngestorDesc

;-------------------------------------------------------------------------
; Custom Setup Type page
;-------------------------------------------------------------------------
; Registered ahead of MUI_PAGE_WELCOME (see file header). Tauri's template
; doesn't expose page-list customization, so we use a top-level Page directive
; here -- legal because installer_hooks is `!include`d at template top scope.
Page custom AIMemorySetupTypePageShow AIMemorySetupTypePageLeave

Function AIMemorySetupTypePageShow
  ; Default selection sticks unless the user changes it.
  StrCpy $AIMemoryInstallMode "full"

  !insertmacro MUI_HEADER_TEXT "Setup Type" "Choose how to install AIMemory on this machine."

  nsDialogs::Create 1018
  Pop $SetupTypeDialog
  ${If} $SetupTypeDialog == error
    Abort
  ${EndIf}

  ${NSD_CreateRadioButton} 0 0u 100% 12u "Full Install (recommended)"
  Pop $SetupTypeRadioFull
  ${NSD_Check} $SetupTypeRadioFull

  ${NSD_CreateLabel} 16u 14u 90% 26u "Installs the AIMemory dashboard plus both background services. Use this on the machine that owns your code index."
  Pop $SetupTypeLabelFullDesc

  ${NSD_CreateRadioButton} 0 46u 100% 12u "Ingestor-only (Remote node)"
  Pop $SetupTypeRadioIngestor

  ${NSD_CreateLabel} 16u 60u 90% 26u "Installs only the ingestor service and a one-time pairing wizard. Use this on a machine that should push code-index events to a remote AIMemory primary on your LAN."
  Pop $SetupTypeLabelIngestorDesc

  nsDialogs::Show
FunctionEnd

Function AIMemorySetupTypePageLeave
  ${NSD_GetState} $SetupTypeRadioIngestor $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $AIMemoryInstallMode "ingestor-only"
  ${Else}
    StrCpy $AIMemoryInstallMode "full"
  ${EndIf}
FunctionEnd

;-------------------------------------------------------------------------
; .NET 10 prerequisite check
;-------------------------------------------------------------------------
!macro NSIS_HOOK_PREINSTALL
  DetailPrint "Checking for .NET 10 runtime..."
  ReadRegStr $0 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost" "Version"
  ${If} $0 == ""
    MessageBox MB_YESNO|MB_ICONQUESTION \
      ".NET 10 runtime is required by AIMemory but was not detected.$\n$\nDownload and install it now?" \
      IDYES install_dotnet IDNO abort_dotnet
    abort_dotnet:
      MessageBox MB_OK|MB_ICONSTOP \
        "Setup cannot continue without .NET 10. Please install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run setup again."
      Abort
    install_dotnet:
      ExecShell "open" "https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe"
      MessageBox MB_OK|MB_ICONINFORMATION \
        "After installing .NET 10, click OK to continue. If the installer needs more time, run AIMemory setup again later."
  ${EndIf}
!macroend

;-------------------------------------------------------------------------
; Service registration after install (mode-aware)
;-------------------------------------------------------------------------
!macro NSIS_HOOK_POSTINSTALL
  DetailPrint "Selected install mode: $AIMemoryInstallMode"

  ; NSIS doesn't expose %ProgramData% as a built-in variable, so resolve via
  ; the environment. Fall back to the canonical Windows path if the env var
  ; is unexpectedly missing.
  ReadEnvStr $1 "PROGRAMDATA"
  ${If} $1 == ""
    StrCpy $1 "C:\ProgramData"
  ${EndIf}

  ; --- ProgramData layout (always created; ingestor needs Ingestor + logs +
  ;     db; full needs Api too -- creating Api in ingestor-only mode is
  ;     harmless and keeps re-runs idempotent if the user later switches.).
  DetailPrint "Creating ProgramData directories under $1\AIMemory..."
  CreateDirectory "$1\AIMemory"
  CreateDirectory "$1\AIMemory\Api"
  CreateDirectory "$1\AIMemory\Ingestor"
  CreateDirectory "$1\AIMemory\logs"
  CreateDirectory "$1\AIMemory\db"

  ; --- app-mode.json: phase 9's Tauri shell reads this to switch between
  ;     dashboard and ingestor wizard. Kebab-case mode value matches
  ;     app_mode.rs's serde rename_all = "kebab-case".
  DetailPrint "Writing app-mode.json..."
  ClearErrors
  FileOpen $0 "$1\AIMemory\app-mode.json" w
  ${If} ${Errors}
    DetailPrint "WARNING: could not write $1\AIMemory\app-mode.json"
  ${Else}
    FileWrite $0 '{$\r$\n  "mode": "$AIMemoryInstallMode"$\r$\n}$\r$\n'
    FileClose $0
  ${EndIf}

  ; --- Persist the mode in the registry so the uninstaller can know which
  ;     services were registered without re-prompting.
  WriteRegStr HKLM "Software\AIMemory" "InstallMode" "$AIMemoryInstallMode"

  ${If} $AIMemoryInstallMode == "ingestor-only"
    ; Tauri's File commands already copied AIMemory.Api.* into $INSTDIR. We
    ; remove them here so the install dir reflects the user's selection and
    ; troubleshooting on a remote node isn't muddied by an unused API binary.
    ; If a particular file isn't present (future builds may not bundle it)
    ; the Delete is a no-op.
    DetailPrint "Pruning API binaries (ingestor-only mode)..."
    Delete "$INSTDIR\AIMemory.Api.exe"
    Delete "$INSTDIR\AIMemory.Api.pdb"
    Delete "$INSTDIR\AIMemory.Api.dll"
    Delete "$INSTDIR\AIMemory.Api.xml"

    ; Replace the Tauri-created Start Menu shortcut with one named for the
    ; ingestor wizard. Same exe target -- the shell detects mode from
    ; app-mode.json at startup.
    DetailPrint "Updating Start Menu shortcut for ingestor-only mode..."
    Delete "$SMPROGRAMS\${PRODUCTNAME}.lnk"
    Delete "$SMPROGRAMS\$AppStartMenuFolder\${PRODUCTNAME}.lnk"
    CreateShortcut "$SMPROGRAMS\AIMemory Ingestor Setup.lnk" "$INSTDIR\${MAINBINARYNAME}.exe"

    ; Register only the ingestor service. No `depend=` on aimemory-api since
    ; the API isn't running on this machine.
    DetailPrint "Registering aimemory-ingestor service..."
    ExecWait 'sc.exe stop aimemory-ingestor'
    ExecWait 'sc.exe delete aimemory-ingestor'
    ExecWait 'sc.exe create aimemory-ingestor binPath= "\"$INSTDIR\AIMemory.Ingestor.exe\"" start= auto DisplayName= "AIMemory Ingestor"'
    ExecWait 'sc.exe description aimemory-ingestor "Watches configured paths and pushes code-index events to a remote AIMemory API."'

    ; Don't auto-start the ingestor in ingestor-only mode -- the wizard will
    ; configure credentials first, then start the service.
    DetailPrint "Skipping service start (wizard will start ingestor after pairing)."
  ${Else}
    ; Full install: everything stays. Original phase 2 behavior.
    DetailPrint "Registering aimemory-api service..."
    ExecWait 'sc.exe stop aimemory-api'
    ExecWait 'sc.exe delete aimemory-api'
    ExecWait 'sc.exe create aimemory-api binPath= "\"$INSTDIR\AIMemory.Api.exe\"" start= auto DisplayName= "AIMemory API"'
    ExecWait 'sc.exe description aimemory-api "Local code-indexer HTTP API for the AIMemory Desktop control panel."'

    DetailPrint "Registering aimemory-ingestor service..."
    ExecWait 'sc.exe stop aimemory-ingestor'
    ExecWait 'sc.exe delete aimemory-ingestor'
    ExecWait 'sc.exe create aimemory-ingestor binPath= "\"$INSTDIR\AIMemory.Ingestor.exe\"" start= auto DisplayName= "AIMemory Ingestor" depend= aimemory-api'
    ExecWait 'sc.exe description aimemory-ingestor "Watches configured paths and pushes code-index events to the AIMemory API."'

    DetailPrint "Starting services..."
    ExecWait 'sc.exe start aimemory-api'
    ExecWait 'sc.exe start aimemory-ingestor'
  ${EndIf}
!macroend

;-------------------------------------------------------------------------
; Service teardown on uninstall
;-------------------------------------------------------------------------
!macro NSIS_HOOK_PREUNINSTALL
  ; Read what the install actually registered. If the registry value is
  ; missing (legacy / pre-phase-10 install) assume "full" and try both.
  ReadRegStr $0 HKLM "Software\AIMemory" "InstallMode"
  ${If} $0 == ""
    StrCpy $0 "full"
  ${EndIf}
  DetailPrint "Uninstalling AIMemory ($0 mode)..."

  DetailPrint "Stopping services..."
  ExecWait 'sc.exe stop aimemory-ingestor'
  ${If} $0 == "full"
    ExecWait 'sc.exe stop aimemory-api'
  ${EndIf}

  DetailPrint "Removing services..."
  ExecWait 'sc.exe delete aimemory-ingestor'
  ${If} $0 == "full"
    ExecWait 'sc.exe delete aimemory-api'
  ${EndIf}

  ; Remove the renamed shortcut if it exists. The Tauri default shortcut is
  ; cleaned up by the template's own uninstall logic.
  Delete "$SMPROGRAMS\AIMemory Ingestor Setup.lnk"
!macroend

!macro NSIS_HOOK_POSTUNINSTALL
  ; Drop only the marker file we own. Intentionally leave the rest of
  ; %ProgramData%\AIMemory in place so the user's DB, checkpoints, and
  ; (in ingestor-only mode) the paired-host appsettings.json survive.
  ; Manual full-purge instructions live in the README.
  ReadEnvStr $1 "PROGRAMDATA"
  ${If} $1 == ""
    StrCpy $1 "C:\ProgramData"
  ${EndIf}
  Delete "$1\AIMemory\app-mode.json"
  DeleteRegValue HKLM "Software\AIMemory" "InstallMode"
  DeleteRegKey /ifempty HKLM "Software\AIMemory"
!macroend
