#requires -Version 7.0
<#
.SYNOPSIS
    Build the AIMemory NSIS installer.

.DESCRIPTION
    Publishes API, Ingestor, and MCP as framework-dependent single-file binaries,
    builds the React SPA into wwwroot, stages everything under
    scripts/installer/staging/, then runs makensis.exe to produce a setup.exe.

.PARAMETER Version
    Installer version label. Embedded in the .exe name, registry, and "Programs &
    Features". Default: 0.2.0

.PARAMETER Configuration
    dotnet publish configuration. Default: Release

.PARAMETER OutputDir
    Where to drop the final setup.exe. Default: scripts/installer/out

.PARAMETER Makensis
    Path to makensis.exe. Default: searches PATH, then C:\Program Files\NSIS, then
    C:\Program Files (x86)\NSIS.

.PARAMETER SkipPublish
    Skip the dotnet publish + npm build steps and use whatever's already staged.
    Useful for iterating on the .nsi script.

.EXAMPLE
    pwsh scripts/installer/build-installer.ps1
    pwsh scripts/installer/build-installer.ps1 -Version 0.3.0 -OutputDir .\dist
#>
[CmdletBinding()]
param(
  [string]$Version = '0.2.0',
  [string]$Configuration = 'Release',
  [string]$OutputDir,
  [string]$Makensis,
  [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Repo paths (resolved relative to this script so it's idempotent).
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir '..\..')
$StageDir = Join-Path $ScriptDir 'staging'
if (-not $OutputDir) { $OutputDir = Join-Path $ScriptDir 'out' }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# Find makensis if not supplied.
if (-not $Makensis) {
  $candidates = @(
    'makensis.exe',
    'C:\Program Files\NSIS\makensis.exe',
    'C:\Program Files (x86)\NSIS\makensis.exe'
  )
  foreach ($c in $candidates) {
    $resolved = (Get-Command $c -ErrorAction SilentlyContinue).Source
    if ($resolved) { $Makensis = $resolved; break }
  }
}
if (-not $Makensis -or -not (Test-Path $Makensis)) {
  throw "makensis.exe not found. Install NSIS (https://nsis.sourceforge.io) or pass -Makensis."
}

Write-Host "Repo:     $RepoRoot"
Write-Host "Stage:    $StageDir"
Write-Host "Output:   $OutputDir"
Write-Host "Makensis: $Makensis"
Write-Host "Version:  $Version"

# ----------------------------- Publish .NET ------------------------------

function Publish-Project([string]$Csproj, [string]$Target) {
  Write-Host ">>> Publishing $Csproj -> $Target" -ForegroundColor Cyan
  Remove-Item -Recurse -Force $Target -ErrorAction SilentlyContinue
  & dotnet publish $Csproj `
    -c $Configuration `
    -r win-x64 `
    -o $Target `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    --nologo
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Csproj" }
}

if (-not $SkipPublish) {
  Remove-Item -Recurse -Force $StageDir -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Path $StageDir | Out-Null

  # Publishing the API triggers PublishRunWebpack which runs npm install +
  # npm run build, so wwwroot/ ends up under staging/Api/wwwroot/ for free —
  # no separate SPA build step needed.
  Publish-Project (Join-Path $RepoRoot 'src\AIMemory.Api\AIMemory.Api.csproj')      (Join-Path $StageDir 'Api')
  Publish-Project (Join-Path $RepoRoot 'src\AIMemory.Ingestor\AIMemory.Ingestor.csproj') (Join-Path $StageDir 'Ingestor')
  Publish-Project (Join-Path $RepoRoot 'src\AIMemory.Mcp\AIMemory.Mcp.csproj')      (Join-Path $StageDir 'Mcp')

  if (-not (Test-Path (Join-Path $StageDir 'Api\wwwroot\index.html'))) {
    throw "API publish completed but wwwroot/index.html is missing. PublishRunWebpack may have failed silently."
  }

  # License + readme
  $license = Join-Path $RepoRoot 'LICENSE'
  if (Test-Path $license) {
    Copy-Item $license (Join-Path $StageDir 'license.txt')
  } else {
    Set-Content -Path (Join-Path $StageDir 'license.txt') -Value 'AGPL-3.0' -Encoding UTF8
  }
  Set-Content -Path (Join-Path $StageDir 'README.txt') -Encoding UTF8 -Value @"
AIMemory $Version

This install registered the following Windows Services:
  - aimemory-api         (full install only)
  - aimemory-ingestor    (always)
  - AIMemory.Mcp.exe     (NOT a service — Claude Code launches it per-session)

To open the web UI on a full install: Start Menu > AIMemory > AIMemory.
To pair an ingestor-only install: Start Menu > AIMemory > Pair Ingestor.

For Claude Code MCP integration, register the MCP server:
    claude mcp add aimemory "$($env:ProgramFiles)\AIMemory\Mcp\AIMemory.Mcp.exe"

Logs:   %ProgramData%\AIMemory\logs\
Config: %ProgramData%\AIMemory\Api\, %ProgramData%\AIMemory\Ingestor\
"@

  # Copy the pairing script + the launcher
  New-Item -ItemType Directory -Force -Path (Join-Path $StageDir 'scripts') | Out-Null
  Copy-Item (Join-Path $ScriptDir 'Pair-AIMemoryIngestor.ps1') (Join-Path $StageDir 'scripts\Pair-AIMemoryIngestor.ps1')
  Copy-Item (Join-Path $ScriptDir 'Open-AIMemory.cmd') (Join-Path $StageDir 'Open-AIMemory.cmd')
}

# ----------------------------- Run makensis ------------------------------

$nsi = Join-Path $ScriptDir 'installer.nsi'
$out = Join-Path $OutputDir "AIMemory_${Version}_x64-setup.exe"

Write-Host ">>> Running makensis -> $out" -ForegroundColor Cyan
& $Makensis `
  "/DVERSION=$Version" `
  "/DSTAGE_DIR=$StageDir" `
  "/DOUTPUT_FILE=$out" `
  $nsi

if ($LASTEXITCODE -ne 0) { throw "makensis failed" }

if (Test-Path $out) {
  $size = [math]::Round(((Get-Item $out).Length / 1MB), 1)
  Write-Host ">>> Built $out (${size} MB)" -ForegroundColor Green
} else {
  throw "makensis reported success but the output file is missing: $out"
}
