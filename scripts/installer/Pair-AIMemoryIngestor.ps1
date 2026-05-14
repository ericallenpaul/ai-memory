#requires -Version 5.1
<#
.SYNOPSIS
    Pair this machine's ingestor with a remote AIMemory primary.

.DESCRIPTION
    Headless replacement for the old Tauri pairing wizard. Prompts for the three
    values revealed on the primary's Distributed page (endpoint, API key, cert
    fingerprint), validates the TLS pin, calls POST /api/pairings, writes the
    ingestor's appsettings.json, then starts the aimemory-ingestor service.

.PARAMETER Endpoint
    Primary's URL, e.g. https://192.168.1.50:5219. Prompted if omitted.

.PARAMETER ApiKey
    The aimemory_... key shown on the primary. Prompted (masked) if omitted.

.PARAMETER Fingerprint
    SHA-256 cert fingerprint from the primary. Accepts either colon-separated
    or plain hex. Prompted if omitted.

.PARAMETER FriendlyName
    Optional display name shown on the primary. Defaults to $env:COMPUTERNAME.

.PARAMETER NonInteractive
    Fail instead of prompting for any missing parameter. Useful for scripted
    deployment via group policy or DSC.

.EXAMPLE
    Pair-AIMemoryIngestor.ps1

.EXAMPLE
    Pair-AIMemoryIngestor.ps1 -Endpoint https://192.168.1.50:5219 `
      -ApiKey aimemory_... -Fingerprint a1:b2:c3:... -FriendlyName "laptop"
#>
[CmdletBinding()]
param(
  [string]$Endpoint,
  [string]$ApiKey,
  [string]$Fingerprint,
  [string]$FriendlyName = $env:COMPUTERNAME,
  [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Required([string]$Prompt, [bool]$Mask = $false) {
  if ($NonInteractive) {
    throw "Missing required value: $Prompt (running -NonInteractive)"
  }
  if ($Mask) {
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    return [System.Net.NetworkCredential]::new('', $secure).Password
  }
  return Read-Host -Prompt $Prompt
}

function Normalize-Fingerprint([string]$raw) {
  if ([string]::IsNullOrWhiteSpace($raw)) { return '' }
  $clean = ($raw -replace '[^0-9a-fA-F]', '').ToLowerInvariant()
  if ($clean.Length -ne 64) {
    throw "Cert fingerprint must be 64 hex chars (SHA-256). Got $($clean.Length) chars after stripping separators."
  }
  return $clean
}

# Collect inputs
Write-Host "=== AIMemory ingestor pairing ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "On the primary, go to the Distributed page, toggle 'Allow remote"
Write-Host "ingestors', pick a bind interface, and confirm. The page reveals"
Write-Host "three values once — paste them here."
Write-Host ""

if (-not $Endpoint) { $Endpoint = Read-Required 'Endpoint URL (e.g. https://192.168.1.50:5219)' }
if (-not $ApiKey) { $ApiKey = Read-Required 'API key (aimemory_...)' $true }
if (-not $Fingerprint) { $Fingerprint = Read-Required 'Cert fingerprint (SHA-256, colon-separated or plain hex)' }
if (-not $FriendlyName) { $FriendlyName = Read-Required "Friendly name [default: $env:COMPUTERNAME]" }

$Fingerprint = Normalize-Fingerprint $Fingerprint

# TLS pin: install a per-process callback that compares the leaf cert's SHA-256
# to the user-supplied value. Any mismatch aborts before any HTTP request goes out.
Add-Type -TypeDefinition @"
using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public static class AimemoryPin {
  public static string ExpectedFingerprint = "";
  public static bool LastMatched;

  public static HttpClient Build(string expected) {
    ExpectedFingerprint = expected.ToLowerInvariant();
    var handler = new HttpClientHandler();
    handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => {
      if (cert == null) { LastMatched = false; return false; }
      using (var sha = SHA256.Create()) {
        var hash = sha.ComputeHash(cert.RawData);
        var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        LastMatched = (hex == ExpectedFingerprint);
        return LastMatched;
      }
    };
    return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
  }
}
"@

$client = [AimemoryPin]::Build($Fingerprint)
$client.DefaultRequestHeaders.Add('X-AIMemory-Api-Key', $ApiKey)

# Health probe: TLS pin + auth both validated in one call.
Write-Host ""
Write-Host "Validating connection..." -ForegroundColor Cyan
try {
  $healthResp = $client.GetAsync("$Endpoint/api/health").GetAwaiter().GetResult()
} catch {
  throw "Could not reach $Endpoint. Underlying error: $($_.Exception.InnerException.Message ?? $_.Exception.Message)"
}
if (-not [AimemoryPin]::LastMatched) {
  throw "TLS fingerprint did not match. Connection aborted before any data was sent."
}
if (-not $healthResp.IsSuccessStatusCode) {
  throw "Health probe returned $($healthResp.StatusCode). Re-check the API key."
}
Write-Host "  TLS pin OK and auth OK." -ForegroundColor Green

# Read the local ingestor's host_id.
$ingestorExe = Join-Path $PSScriptRoot '..\Ingestor\AIMemory.Ingestor.exe'
if (-not (Test-Path $ingestorExe)) {
  # When run from %SMPROGRAMS%, $PSScriptRoot is the install dir's scripts/ subfolder.
  $ingestorExe = Join-Path $PSScriptRoot '..\..\AIMemory\Ingestor\AIMemory.Ingestor.exe'
}
if (-not (Test-Path $ingestorExe)) {
  throw "AIMemory.Ingestor.exe not found — try running this from the install dir's scripts\ folder."
}
$hostId = (& $ingestorExe '--print-host-id').Trim()
if (-not $hostId) { throw "AIMemory.Ingestor.exe --print-host-id returned no value." }

# POST /api/pairings
Write-Host "Registering pairing with primary..." -ForegroundColor Cyan
$body = @{
  hostId       = $hostId
  friendlyName = $FriendlyName
  osKind       = 'windows'
  version      = '0.2.0'
} | ConvertTo-Json -Depth 4

$content = New-Object System.Net.Http.StringContent($body, [System.Text.Encoding]::UTF8, 'application/json')
$pairResp = $client.PostAsync("$Endpoint/api/pairings", $content).GetAwaiter().GetResult()
if (-not [AimemoryPin]::LastMatched) {
  throw "TLS fingerprint mismatched on pairing call. Aborted."
}
$pairBody = $pairResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
if (-not $pairResp.IsSuccessStatusCode) {
  throw "POST /api/pairings returned $($pairResp.StatusCode): $pairBody"
}
$pairing = $pairBody | ConvertFrom-Json
Write-Host "  Paired. pairingId=$($pairing.pairingId)" -ForegroundColor Green

# Write the ingestor's appsettings.json
$configDir = Join-Path $env:ProgramData 'AIMemory\Ingestor'
if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
$configPath = Join-Path $configDir 'appsettings.json'

$configObj = @{
  Ingestor = @{
    Mode = 'Remote'
    Remote = @{
      Endpoint              = $Endpoint
      ApiKey                = $ApiKey
      PinnedCertFingerprint = $Fingerprint
    }
    ClientId   = $env:COMPUTERNAME
    BatchSize  = 100
    Sources    = @()
  }
}
$configObj | ConvertTo-Json -Depth 6 | Set-Content -Path $configPath -Encoding UTF8
Write-Host "  Wrote $configPath" -ForegroundColor Green

# Start the service
Write-Host "Starting aimemory-ingestor..." -ForegroundColor Cyan
& sc.exe start aimemory-ingestor | Out-Null
if ($LASTEXITCODE -ne 0) {
  Write-Warning "sc.exe start aimemory-ingestor returned $LASTEXITCODE. Start it manually if needed."
} else {
  Write-Host "  Started." -ForegroundColor Green
}

Write-Host ""
Write-Host "Pairing complete. The ingestor will start forwarding events shortly."
