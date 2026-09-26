<#
.SYNOPSIS
Authoritative Build, Publish, and Verify script for Error Optimizer V4 (GUI + Backend Service + CLI).
#>

$ErrorActionPreference = "Stop"

$baseDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($baseDir)) {
    $baseDir = "d:\SOFTWARE PROJECTS\ERROR OPTIMIZER"
}

$guiProj = Join-Path $baseDir "src\BiosOptimizer.GUI\BiosOptimizer.GUI.csproj"
$svcProj = Join-Path $baseDir "src\BiosOptimizer.Service\BiosOptimizer.Service.csproj"
$cliProj = Join-Path $baseDir "src\BiosOptimizer.CLI\BiosOptimizer.CLI.csproj"

$dotnetExe = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnetExe)) {
    $dotnetExe = "dotnet"
}

$distDir = Join-Path $baseDir "dist\win-x64"
$coreDir = Join-Path $distDir "core"
$guiExePath = Join-Path $distDir "ErrorOptimizer.exe"
$v4GuiExePath = Join-Path $distDir "ERROR OPTIMIZER.exe"
$svcExePath = Join-Path $coreDir "BiosOptimizer.Service.exe"
$cliExePath = Join-Path $coreDir "BiosOptimizer.CLI.exe"
$emptyStandbyPath = Join-Path $distDir "EmptyStandbyList.exe"

Write-Host "============================================================"
Write-Host "1. STOPPING RUNNING PROCESSES"
Write-Host "============================================================"
$procs = Get-Process -Name "ErrorOptimizer", "ERROR OPTIMIZER", "BiosOptimizer.Service", "BiosOptimizer.CLI" -ErrorAction SilentlyContinue
if ($procs) {
    Write-Host "Stopping $($procs.Count) running instance(s)..."
    $procs | Stop-Process -Force
    Start-Sleep -Seconds 2
} else {
    Write-Host "No ErrorOptimizer, Service, or CLI processes running."
}

Write-Host "============================================================"
Write-Host "2. CLEANING DIST FOLDER SAFELY"
Write-Host "============================================================"
if (Test-Path $distDir) {
    # Backup EmptyStandbyList.exe if present
    $hasStandby = Test-Path $emptyStandbyPath
    if ($hasStandby) {
        $tempStandby = Join-Path $env:TEMP "EmptyStandbyList.exe"
        Copy-Item $emptyStandbyPath $tempStandby -Force
    }

    # Clean dist
    Get-ChildItem -Path $distDir | Remove-Item -Recurse -Force

    # Restore EmptyStandbyList.exe
    if ($hasStandby -and (Test-Path $tempStandby)) {
        New-Item -ItemType Directory -Force -Path $distDir | Out-Null
        Move-Item $tempStandby $emptyStandbyPath -Force
        Write-Host "Preserved and restored: EmptyStandbyList.exe"
    }
} else {
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
}

Write-Host "============================================================"
Write-Host "3. CLEANING BUILD ARTIFACTS"
Write-Host "============================================================"
& $dotnetExe clean $guiProj -c Release
& $dotnetExe clean $svcProj -c Release
& $dotnetExe clean $cliProj -c Release

Write-Host "============================================================"
Write-Host "4. GENERATING BUILD ID"
Write-Host "============================================================"
$timestamp = Get-Date -Format "yyyyMMdd-HHmm"
$randomHex = -join ((48..57) + (65..70) | Get-Random -Count 4 | % {[char]$_})
$buildId = "$timestamp-$randomHex"
Write-Host "UNIFIED BUILD ID: $buildId"

Write-Host "============================================================"
Write-Host "5. PUBLISHING MAIN GUI (SELF-CONTAINED WIN-X64)"
Write-Host "============================================================"
& $dotnetExe publish $guiProj -c Release -r win-x64 --self-contained true -o $distDir /p:InformationalVersion="$buildId" /p:PublishTrimmed=false /p:PublishSingleFile=false

if ($LASTEXITCODE -ne 0) {
    Write-Error "Main GUI publish failed."
    exit 1
}

# Duplicate / Alias to user-facing name 'ERROR OPTIMIZER.exe'
Copy-Item -Path $guiExePath -Destination $v4GuiExePath -Force
Write-Host "Authoritative V4 GUI created at: $v4GuiExePath"

Write-Host "============================================================"
Write-Host "6. PUBLISHING BACKEND SERVICE & CLI (SELF-CONTAINED WIN-X64)"
Write-Host "============================================================"
& $dotnetExe publish $svcProj -c Release -r win-x64 --self-contained true -o $coreDir /p:InformationalVersion="$buildId" /p:PublishTrimmed=false /p:PublishSingleFile=false

if ($LASTEXITCODE -ne 0) {
    Write-Error "Backend Service publish failed."
    exit 1
}

& $dotnetExe publish $cliProj -c Release -r win-x64 --self-contained true -o $coreDir /p:InformationalVersion="$buildId" /p:PublishTrimmed=false /p:PublishSingleFile=false

if ($LASTEXITCODE -ne 0) {
    Write-Error "Backend CLI publish failed."
    exit 1
}

# Copy assets
$assetsSrc = Join-Path $baseDir "assets"
$assetsDst = Join-Path $distDir "assets"
if (Test-Path $assetsSrc) {
    Copy-Item -Path $assetsSrc -Destination $distDir -Recurse -Force
    Write-Host "Copied assets to: $assetsDst"
}

# Copy profiles
$profilesSrc = Join-Path $baseDir "profiles"
if (Test-Path $profilesSrc) {
    Copy-Item -Path $profilesSrc -Destination $distDir -Recurse -Force
    Copy-Item -Path $profilesSrc -Destination $coreDir -Recurse -Force
    Write-Host "Copied profiles to dist and core."
}

Write-Host "============================================================"
Write-Host "7. VERIFYING PUBLISHED FILE SET"
Write-Host "============================================================"
if (-not (Test-Path $guiExePath)) {
    Write-Error "Main GUI executable not found at: $guiExePath"
    exit 1
}
if (-not (Test-Path $v4GuiExePath)) {
    Write-Error "V4 GUI executable not found at: $v4GuiExePath"
    exit 1
}
if (-not (Test-Path $svcExePath)) {
    Write-Error "Backend service executable not found at: $svcExePath"
    exit 1
}
if (-not (Test-Path $cliExePath)) {
    Write-Error "CLI executable not found at: $cliExePath"
    exit 1
}

$guiInfo = Get-Item $guiExePath
$v4GuiInfo = Get-Item $v4GuiExePath
$svcInfo = Get-Item $svcExePath
$cliInfo = Get-Item $cliExePath

Write-Host "MAIN APP EXE:     $guiExePath"
Write-Host "MAIN EXE TIME:    $($guiInfo.LastWriteTime)"
Write-Host "MAIN EXE SIZE:    $($guiInfo.Length) bytes"
Write-Host "V4 ALIAS EXE:     $v4GuiExePath"
Write-Host "V4 EXE TIME:      $($v4GuiInfo.LastWriteTime)"
Write-Host "V4 EXE SIZE:      $($v4GuiInfo.Length) bytes"
Write-Host "SERVICE EXE:      $svcExePath"
Write-Host "SERVICE EXE TIME: $($svcInfo.LastWriteTime)"
Write-Host "SERVICE EXE SIZE: $($svcInfo.Length) bytes"
Write-Host "CLI EXE:          $cliExePath"
Write-Host "CLI EXE TIME:     $($cliInfo.LastWriteTime)"
Write-Host "CLI EXE SIZE:     $($cliInfo.Length) bytes"
Write-Host "BUILD ID:         $buildId"

Write-Host "============================================================"
Write-Host "8. LAUNCHING AUTHORITATIVE EXE & TESTING SERVICE STARTUP"
Write-Host "============================================================"
Start-Process -FilePath $v4GuiExePath
Start-Sleep -Seconds 4

$guiProc = Get-Process -Name "ERROR OPTIMIZER", "ErrorOptimizer" -ErrorAction SilentlyContinue
$svcProc = Get-Process -Name "BiosOptimizer.Service" -ErrorAction SilentlyContinue

if ($guiProc) {
    Write-Host "RUNNING GUI PATH:     $($guiProc[0].Path)" -ForegroundColor Green
} else {
    Write-Error "GUI process failed to start or crashed."
}

if ($svcProc) {
    Write-Host "RUNNING SERVICE PATH: $($svcProc[0].Path)" -ForegroundColor Green
    Write-Host "BACKEND SERVICE DEPLOYMENT VERIFIED" -ForegroundColor Green
    Write-Host "MAIN APP + SERVICE RELEASE VERIFIED" -ForegroundColor Green
} else {
    Write-Warning "BiosOptimizer.Service process not detected yet (may be delayed or named differently)."
}

# Stop test processes before packaging to release file locks
Write-Host "Stopping smoke-test processes for installer packaging..."
Get-Process -Name "ERROR OPTIMIZER", "ErrorOptimizer", "BiosOptimizer.Service", "BiosOptimizer.CLI" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "============================================================"
Write-Host "9. COMPILING INNO SETUP PRODUCTION INSTALLER"
Write-Host "============================================================"
$isccPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $isccPath)) {
    $isccPath = "C:\Program Files\Inno Setup 6\ISCC.exe"
}

if (Test-Path $isccPath) {
    $issFile = Join-Path $baseDir "ErrorOptimizerSetup.iss"
    Write-Host "Compiling Inno Setup script: $issFile"
    & $isccPath $issFile
    if ($LASTEXITCODE -eq 0) {
        $setupExe = Join-Path $baseDir "dist\installer\ErrorOptimizer_V4_Setup.exe"
        if (-not (Test-Path $setupExe)) {
            $setupExe = Join-Path $baseDir "dist\installer\ErrorOptimizer_V3_Setup.exe"
        }
        if (Test-Path $setupExe) {
            $setupInfo = Get-Item $setupExe
            Write-Host "SETUP INSTALLER CREATED: $setupExe" -ForegroundColor Green
            Write-Host "SETUP INSTALLER SIZE:    $([Math]::Round($setupInfo.Length / 1MB, 2)) MB" -ForegroundColor Green
            Write-Host "SETUP INSTALLER TIME:    $($setupInfo.LastWriteTime)" -ForegroundColor Green
        }
    } else {
        Write-Error "Inno Setup compilation failed with code $LASTEXITCODE"
    }
} else {
    Write-Warning "ISCC.exe not found. Skipping installer compilation."
}
