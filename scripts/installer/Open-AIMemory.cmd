@echo off
REM Launches the user's default browser at the local AIMemory web UI.
REM The API picks its listen port on first start and writes it to
REM %ProgramData%\AIMemory\Api\port. We read that file (or fall back to the
REM canonical 5219 used in dev) and open the URL.

setlocal
set "PORT_FILE=%ProgramData%\AIMemory\Api\port"
set "PORT=5219"
if exist "%PORT_FILE%" (
  for /f "usebackq tokens=*" %%P in ("%PORT_FILE%") do set "PORT=%%P"
)

REM If distributed mode is on, the API listens on https + the bind interface;
REM the loopback HTTP listener also stays up so the UI keeps working without
REM the user having to trust the self-signed cert. Always open http://localhost.
start "" "http://localhost:%PORT%/"
