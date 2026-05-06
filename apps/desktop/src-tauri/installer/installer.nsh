; AIMemory Desktop — NSIS installer hooks.
; Tauri passes the user's bundle context, so the binaries are already at $INSTDIR.
; Our job: ensure .NET 10 is installed, register the two Windows Services, and
; reverse all of that on uninstall.

!include LogicLib.nsh

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
; Service registration after install
;-------------------------------------------------------------------------
!macro NSIS_HOOK_POSTINSTALL
  DetailPrint "Creating ProgramData directories..."
  CreateDirectory "$APPDATA\..\..\ProgramData\AIMemory\Api"
  CreateDirectory "$APPDATA\..\..\ProgramData\AIMemory\Ingestor"
  CreateDirectory "$APPDATA\..\..\ProgramData\AIMemory\logs"
  CreateDirectory "$APPDATA\..\..\ProgramData\AIMemory\db"

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
!macroend

;-------------------------------------------------------------------------
; Service teardown on uninstall
;-------------------------------------------------------------------------
!macro NSIS_HOOK_PREUNINSTALL
  DetailPrint "Stopping services..."
  ExecWait 'sc.exe stop aimemory-ingestor'
  ExecWait 'sc.exe stop aimemory-api'

  DetailPrint "Removing services..."
  ExecWait 'sc.exe delete aimemory-ingestor'
  ExecWait 'sc.exe delete aimemory-api'
!macroend

!macro NSIS_HOOK_POSTUNINSTALL
  ; Intentionally leave %ProgramData%\AIMemory in place so the user's DB and
  ; checkpoints survive a reinstall. Manual removal is documented in the README.
!macroend
