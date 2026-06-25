#!/usr/bin/env pwsh
# Build the mcptx CLI and pack it into a Claude Desktop .mcpb bundle.
#
# Default = self-contained single-file (NO .NET runtime required on the target;
# ~53 MB win-x64, under the size that fails Claude Desktop install). Pass
# -FrameworkDependent for the tiny (~5 MB) build that needs the .NET 10 runtime.
#
# Cross-platform (pwsh on Windows/macOS/Linux). Publish each RID on its native
# runner to avoid cross-arch ReadyToRun issues. Outputs to dist/ (gitignored):
#   dist/mcptransfer-<version>-<rid>-<variant>.mcpb
#   dist/mcptx-<version>-<rid>[.exe]              (the bare binary)
#
# Usage:
#   ./tools/build-mcpb.ps1                         # win-x64 self-contained
#   ./tools/build-mcpb.ps1 -Runtime osx-arm64
#   ./tools/build-mcpb.ps1 -FrameworkDependent
[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [string]$OutDir = "dist",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "src/MCPTransfer.Agent/MCPTransfer.Agent.csproj"

# Version: explicit arg wins, else read Directory.Build.props (single source).
if ([string]::IsNullOrWhiteSpace($Version)) {
    $props = Get-Content (Join-Path $repo "Directory.Build.props") -Raw
    if ($props -match '<Version>([^<]+)</Version>') { $Version = $Matches[1].Trim() }
    else { throw "Could not read <Version> from Directory.Build.props; pass -Version." }
}

$isWin = $Runtime -like "win-*"
$exeName = if ($isWin) { "mcptx.exe" } else { "mcptx" }
$platform = switch -Wildcard ($Runtime) {
    "win-*"   { "win32";  break }
    "osx-*"   { "darwin"; break }
    "linux-*" { "linux";  break }
    default   { throw "Unsupported runtime '$Runtime'." }
}
$variant = if ($FrameworkDependent) { "fwdep" } else { "selfcontained" }

$outAbs = Join-Path $repo $OutDir
New-Item -ItemType Directory -Force $outAbs | Out-Null
$publishDir = Join-Path $outAbs "publish-$Runtime-$variant"
Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue

Write-Host "Publishing $Runtime ($variant), version $Version ..."
$publishArgs = @("publish", $proj, "-c", "Release", "-r", $Runtime, "-o", $publishDir, "-p:Version=$Version")
if ($FrameworkDependent) {
    # Single-file too (managed dlls embedded into one small exe), just NOT the
    # runtime — so the .mcpb is one file and stays small, but needs .NET 10.
    # (Compression is self-contained-only: NETSDK1176, so it's omitted here.)
    $publishArgs += @(
        "--no-self-contained",
        "-p:PublishSingleFile=true",
        "-p:InvariantGlobalization=true"
    )
} else {
    $publishArgs += @(
        "--self-contained",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:InvariantGlobalization=true",
        "-p:PublishReadyToRun=true"
    )
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

$exePath = Join-Path $publishDir $exeName
if (-not (Test-Path $exePath)) { throw "Expected published binary not found: $exePath" }

# --- assemble .mcpb staging: /manifest.json + /server/<exe> ---
$stage = Join-Path $outAbs "mcpb-stage-$Runtime-$variant"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
$serverDir = Join-Path $stage "server"
New-Item -ItemType Directory -Force $serverDir | Out-Null
Copy-Item $exePath (Join-Path $serverDir $exeName)

# Manifest: load the tracked base and inject per-build fields.
$manifest = Get-Content (Join-Path $repo "packaging/mcpb/manifest.base.json") -Raw | ConvertFrom-Json
$manifest.version = $Version
$manifest.server.entry_point = "server/$exeName"
$manifest.server.mcp_config.command = '${__dirname}/server/' + $exeName
$manifest.compatibility.platforms = @($platform)
# Be honest about the runtime requirement per variant.
$runtimeNote = if ($FrameworkDependent) {
    "Requires the .NET 10 runtime installed. "
} else {
    "Self-contained build - NO .NET runtime required. "
}
$manifest.long_description = $runtimeNote + $manifest.long_description
$manifestJson = $manifest | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText((Join-Path $stage "manifest.json"), $manifestJson)

# --- zip → .mcpb (explicit forward-slash entry names: Compress-Archive writes
#     backslashes on Windows, which breaks .mcpb extraction on macOS/Linux) ---
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
$mcpb = Join-Path $outAbs "mcptransfer-$Version-$Runtime-$variant.mcpb"
Remove-Item -Force $mcpb -ErrorAction SilentlyContinue
$zip = [System.IO.Compression.ZipFile]::Open($mcpb, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip, (Join-Path $stage "manifest.json"), "manifest.json") | Out-Null
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip, (Join-Path $serverDir $exeName), "server/$exeName") | Out-Null
}
finally {
    $zip.Dispose()
}

# --- also drop the bare binary as a release asset ---
$bareName = if ($isWin) { "mcptx-$Version-$Runtime.exe" } else { "mcptx-$Version-$Runtime" }
$barePath = Join-Path $outAbs $bareName
Copy-Item $exePath $barePath -Force

$mcpbMB = [math]::Round((Get-Item $mcpb).Length / 1MB, 1)
Write-Host ""
Write-Host "Built:"
Write-Host "  $mcpb  ($mcpbMB MB)"
Write-Host "  $barePath"
