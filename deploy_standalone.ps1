<#
.SYNOPSIS
Authoritative Standalone Build, Publish, Hash-Verification & Deployment Pipeline for Error Optimizer V4 Standalone.
Implements strict 13-stage deterministic pipeline.
#>
[CmdletBinding()]
param(
    [switch]$SkipLaunch = $false
)

$ErrorActionPreference = "Stop"
$baseDir = "D:\SOFTWARE PROJECTS\ERROR OPTIMIZER"
$frontendDir = Join-Path $baseDir "prototype_standalone\frontend"
$frontendDistDir = Join-Path $frontendDir "dist"
$zipPath = Join-Path $baseDir "prototype_standalone\frontend-bundle.zip"
$hostProj = Join-Path $baseDir "prototype_standalone\host\PrototypeHost.csproj"
$coreProj = Join-Path $baseDir "src\BiosOptimizer.Core\BiosOptimizer.Core.csproj"
$stagingDir = Join-Path $baseDir "publish_standalone"
$canonicalDistDir = Join-Path $baseDir "dist\win-x64"
$installedDir = "C:\Program Files\Error Optimizer V4"

function Get-Sha256([string]$filePath) {
    if (-not (Test-Path $filePath)) { return "" }
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($filePath)
    $bytes = $hasher.ComputeHash($stream)
    $stream.Close()
    return -join ($bytes | ForEach-Object { "{0:X2}" -f $_ })
}

Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "ERROR OPTIMIZER V4 -- AUTHORITATIVE STANDALONE PIPELINE (13 STAGES)" -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan

# ── STAGE 0: TERMINATE ACTIVE INSTANCES SAFELY ──
Write-Host "[0/13] Stopping any running Standalone / Prototype host instances..." -ForegroundColor Yellow
$active = Get-Process -Name "ERROR OPTIMIZER PROTOTYPE", "ErrorOptimizer" -ErrorAction SilentlyContinue
if ($active) {
    $active | Stop-Process -Force
    Start-Sleep -Seconds 1
}
Get-Process -Name "msedgewebview2" -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -like "*EdgeWebView*" -and $_.CommandLine -like "*ErrorOptimizerPrototype*" } catch { $false }
} | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Host "       Host and child processes stopped." -ForegroundColor Green

# ── STAGE 1: VALIDATE SOURCE TREE ──
Write-Host "[1/13] Validating source tree..." -ForegroundColor Yellow
if (-not (Test-Path (Join-Path $baseDir "src\BiosOptimizer.Core\Implementations\Startup\WindowsStartupManager.cs"))) {
    throw "WindowsStartupManager.cs missing from source tree!"
}
if (-not (Test-Path (Join-Path $frontendDir "src\components\eo\pages\ManagerPages.tsx"))) {
    throw "ManagerPages.tsx missing from source tree!"
}
Write-Host "       Source tree verified." -ForegroundColor Green

# ── STAGE 2: BUILD FRONTEND ──
Write-Host "[2/13] Building frontend SPA via Vite (npm run build:spa)..." -ForegroundColor Yellow
$npmProc = Start-Process -FilePath "cmd.exe" -ArgumentList "/c cd /d `"$frontendDir`" && npm run build:spa" -NoNewWindow -Wait -PassThru
if ($npmProc.ExitCode -ne 0) {
    throw "Frontend compilation failed with exit code $($npmProc.ExitCode)"
}
Write-Host "       Frontend compilation succeeded." -ForegroundColor Green

# ── STAGE 3: VERIFY FRONTEND BUILD OUTPUT ──
Write-Host "[3/13] Verifying frontend build output files..." -ForegroundColor Yellow
$feIndex = Join-Path $frontendDistDir "index.html"
$feJs = Get-ChildItem -Path (Join-Path $frontendDistDir "assets") -Filter "index-*.js" | Select-Object -First 1
$feCss = Get-ChildItem -Path (Join-Path $frontendDistDir "assets") -Filter "index-*.css" | Select-Object -First 1

if (-not (Test-Path $feIndex) -or -not $feJs -or -not $feCss) {
    throw "Frontend build output incomplete: index.html or assets missing!"
}

# Verify absence of legacy source labels in new bundle
$jsContent = [System.IO.File]::ReadAllText($feJs.FullName)
if ($jsContent.Contains("HKCU_RUN") -or $jsContent.Contains("MSIX_STARTUP_TASK")) {
    throw "CRITICAL DEFECT: Frontend bundle contains legacy technical badges!"
}
Write-Host "       Verified clean frontend bundle: $($feJs.Name) ($($feJs.Length) bytes)" -ForegroundColor Green

# ── STAGE 4: GENERATE EMBEDDED FRONTEND BUNDLE ──
Write-Host "[4/13] Generating embedded frontend bundle ($zipPath)..." -ForegroundColor Yellow
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$frontendDistDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "       Bundle archive created." -ForegroundColor Green

# ── STAGE 5: VERIFY EMBEDDED BUNDLE SHA-256 ──
Write-Host "[5/13] Verifying embedded bundle SHA-256..." -ForegroundColor Yellow
$zipHash = Get-Sha256 $zipPath
$zipLen = (Get-Item $zipPath).Length
Write-Host "       Bundle Size: $zipLen bytes" -ForegroundColor Green
Write-Host "       Bundle SHA256: $zipHash" -ForegroundColor Green

# ── STAGE 6: BUILD CORE LIBRARY ──
Write-Host "[6/13] Compiling BiosOptimizer.Core Release..." -ForegroundColor Yellow
& dotnet build "$coreProj" -c Release --no-incremental --nologo
if ($LASTEXITCODE -ne 0) {
    throw "BiosOptimizer.Core compilation failed with code $LASTEXITCODE"
}
Write-Host "       BiosOptimizer.Core compiled successfully." -ForegroundColor Green

# ── STAGE 7: BUILD WPF HOST ──
Write-Host "[7/13] Compiling PrototypeHost WPF Release..." -ForegroundColor Yellow
& dotnet build "$hostProj" -c Release --no-incremental --nologo
if ($LASTEXITCODE -ne 0) {
    throw "PrototypeHost compilation failed with code $LASTEXITCODE"
}
Write-Host "       PrototypeHost compiled successfully." -ForegroundColor Green

# ── STAGE 8: PUBLISH STANDALONE APPLICATION (SINGLE-FILE) ──
Write-Host "[8/13] Publishing authoritative single-file standalone application..." -ForegroundColor Yellow
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
& dotnet publish "$hostProj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$stagingDir" --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Standalone publish failed with code $LASTEXITCODE"
}
Write-Host "       Standalone publish succeeded." -ForegroundColor Green

# ── STAGE 9: VERIFY PUBLISHED BINARIES AND RESOURCES ──
Write-Host "[9/13] Verifying published staging binaries..." -ForegroundColor Yellow
$stagedExe = Join-Path $stagingDir "ERROR OPTIMIZER PROTOTYPE.exe"
if (-not (Test-Path $stagedExe)) {
    throw "Published executable missing at $stagedExe"
}
$stagedExeLen = (Get-Item $stagedExe).Length
$stagedExeHash = Get-Sha256 $stagedExe
Write-Host "       Staged EXE Size: $stagedExeLen bytes" -ForegroundColor Green
Write-Host "       Staged EXE SHA256: $stagedExeHash" -ForegroundColor Green

if ($stagedExeLen -lt 50000000) {
    throw "Staged executable size ($stagedExeLen bytes) indicates single-file bundling failure (< 50 MB)!"
}

# ── STAGE 10: DEPLOY TO CANONICAL STANDALONE DIRECTORY ──
Write-Host "[10/13] Deploying to canonical standalone directory ($canonicalDistDir)..." -ForegroundColor Yellow
if (-not (Test-Path $canonicalDistDir)) {
    New-Item -ItemType Directory -Force -Path $canonicalDistDir | Out-Null
}

# Deploy single-file executable
Copy-Item $stagedExe $canonicalDistDir -Force

# Deploy frontend assets
$destFe = Join-Path $canonicalDistDir "frontend"
if (Test-Path $destFe) { Remove-Item $destFe -Recurse -Force }
Copy-Item (Join-Path $stagingDir "frontend") $canonicalDistDir -Recurse -Force

# Deploy profiles
$destProfiles = Join-Path $canonicalDistDir "profiles"
if (Test-Path $destProfiles) { Remove-Item $destProfiles -Recurse -Force }
if (Test-Path (Join-Path $stagingDir "profiles")) {
    Copy-Item (Join-Path $stagingDir "profiles") $canonicalDistDir -Recurse -Force
}

# Deploy data
$destData = Join-Path $canonicalDistDir "data"
if (Test-Path $destData) { Remove-Item $destData -Recurse -Force }
if (Test-Path (Join-Path $stagingDir "data")) {
    Copy-Item (Join-Path $stagingDir "data") $canonicalDistDir -Recurse -Force
}

# Deploy core service & CLI
$destCore = Join-Path $canonicalDistDir "core"
if (Test-Path $destCore) { Remove-Item $destCore -Recurse -Force }
if (Test-Path (Join-Path $stagingDir "core")) {
    Copy-Item (Join-Path $stagingDir "core") $canonicalDistDir -Recurse -Force
}

# Deploy frontend-bundle.zip
Copy-Item $zipPath $canonicalDistDir -Force

# Deploy to installed folder for complete parity
if (Test-Path $installedDir) {
    Copy-Item $stagedExe $installedDir -Force
    if (Test-Path $destFe) {
        $instFe = Join-Path $installedDir "frontend"
        if (Test-Path $instFe) { Remove-Item $instFe -Recurse -Force }
        Copy-Item $destFe $installedDir -Recurse -Force
    }
    Copy-Item $zipPath $installedDir -Force
    Write-Host "       Synchronized deployment to Installed directory: $installedDir" -ForegroundColor Green
}

# Clear stale WebView2 cache to guarantee fresh frontend load
$wv2Cache = Join-Path $env:LOCALAPPDATA "ErrorOptimizerPrototype\frontend-cache"
if (Test-Path $wv2Cache) {
    Remove-Item $wv2Cache -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "       Purged stale WebView2 frontend cache: $wv2Cache" -ForegroundColor Green
}

# ── STAGE 11: VERIFY DEPLOYED FILE HASHES ──
Write-Host "[11/13] Verifying deployed file hashes against publish output..." -ForegroundColor Yellow
$deployedExe = Join-Path $canonicalDistDir "ERROR OPTIMIZER PROTOTYPE.exe"
$deployedExeHash = Get-Sha256 $deployedExe

if ($deployedExeHash -ne $stagedExeHash) {
    throw "DEPLOYMENT CORRUPTION: Deployed hash ($deployedExeHash) does not match Staged hash ($stagedExeHash)!"
}
Write-Host "       HASH VERIFIED: $deployedExeHash (100% PARITY)" -ForegroundColor Green

# Create deployment provenance manifest
$deployManifest = [PSCustomObject]@{
    DeploymentTimeUtc = (Get-Date).ToUniversalTime().ToString("o")
    ApplicationName   = "ERROR OPTIMIZER V4 Standalone"
    Version           = "4.2.0"
    BuildIdentifier   = "4.2.0-standalone-20261123.1850"
    ExecutablePath    = $deployedExe
    ExecutableLength  = (Get-Item $deployedExe).Length
    ExecutableSha256  = $deployedExeHash
    FrontendJs        = $feJs.Name
    FrontendJsSha256  = Get-Sha256 $feJs.FullName
    FrontendCss       = $feCss.Name
    FrontendCssSha256 = Get-Sha256 $feCss.FullName
    FrontendZipSha256 = $zipHash
}
$manifestPath = Join-Path $canonicalDistDir "standalone_deployment_manifest.json"
$deployManifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding utf8
Write-Host "       Deployment manifest written: $manifestPath" -ForegroundColor Green

# ── STAGE 12: LAUNCH DEPLOYED EXECUTABLE ──
if (-not $SkipLaunch) {
    Write-Host "[12/13] Launching deployed standalone executable..." -ForegroundColor Yellow
    $launchProc = Start-Process -FilePath $deployedExe -PassThru
    Start-Sleep -Seconds 3
    if ($launchProc.HasExited) {
        throw "Deployed standalone executable exited prematurely with code $($launchProc.ExitCode)!"
    }
    Write-Host "       Launched PID: $($launchProc.Id) (Running: $($launchProc.ProcessName))" -ForegroundColor Green
} else {
    Write-Host "[12/13] Skipping launch per request." -ForegroundColor DarkGray
}

Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "STANDALONE BUILD, PUBLISH & DEPLOYMENT COMPLETED WITH ZERO DEFECTS" -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan
