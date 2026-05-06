# Publish the .NET binaries the Tauri installer bundles.
#
# Output: ../publish/<rid>/<binary>.exe
# Usage: from repo root, run `.\apps\desktop\scripts\build-binaries.ps1`
#
# Phase 0 set up the framework-dependent + single-file publish profile so each
# binary is ~5-10MB instead of ~200MB. The installer's responsibility is to
# detect/install the .NET 10 runtime that these binaries depend on.

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path "$PSScriptRoot\..\..\.."
$Rid = if ($env:AIMEMORY_RID) { $env:AIMEMORY_RID } else { "win-x64" }
$Output = Join-Path $RepoRoot "apps\desktop\publish\$Rid"

Write-Host "Publishing AIMemory binaries to $Output (RID: $Rid)..." -ForegroundColor Cyan

$Projects = @(
    "AIMemory.Api",
    "AIMemory.Ingestor",
    "AIMemory.Mcp"
)

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output | Out-Null

foreach ($p in $Projects) {
    $proj = Join-Path $RepoRoot "src\$p\$p.csproj"
    Write-Host "  -> $p" -ForegroundColor Yellow
    dotnet publish $proj `
        -c Release `
        -r $Rid `
        --self-contained false `
        -p:PublishSingleFile=true `
        -o $Output `
        --nologo `
        -v quiet
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $p" }
}

Write-Host "Done. Binaries in $Output." -ForegroundColor Green
Get-ChildItem $Output -Filter "*.exe" | ForEach-Object {
    $sizeMb = [math]::Round($_.Length / 1MB, 1)
    Write-Host ("  {0,-30} {1} MB" -f $_.Name, $sizeMb)
}
