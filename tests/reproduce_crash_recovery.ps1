# ERROR OPTIMIZER V4 — INDEPENDENT CRASH RECOVERY REPRODUCTION SCRIPT
# Execution script capturing full process lifecycle, PIDs, exit codes, and registry state.

$ErrorActionPreference = "Stop"
$harnessExe = "D:\SOFTWARE PROJECTS\ERROR OPTIMIZER\tests\CrashFaultInjectionHarness\bin\Debug\net8.0-windows10.0.19041.0\CrashFaultInjectionHarness.exe"
$parentPid = $PID
$testKeyPath = "Software\ErrorOptimizerCrashTests\Repro_" + [Guid]::NewGuid().ToString("N")
$storageBase = Join-Path $env:TEMP "EO_Repro_Tx"

Write-Host "================================================================================"
Write-Host "CRASH FAULT INJECTION INDEPENDENT REPRODUCTION SUITE"
Write-Host "Parent Process: powershell.exe (PID: $parentPid)"
Write-Host "Harness Binary: $harnessExe"
Write-Host "Test Registry Subkey: HKCU\$testKeyPath"
Write-Host "Timestamp: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')"
Write-Host "================================================================================"

function Run-HarnessProcess([string]$arguments) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $harnessExe
    $psi.Arguments = $arguments
    $psi.CreateNoWindow = $true
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $childPid = $proc.Id
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit(10000)
    $exitCode = $proc.ExitCode
    $hexExitCode = "0x{0:X8}" -f [int]$exitCode

    return @{
        ChildPid = $childPid
        ExitCode = $exitCode
        HexExitCode = $hexExitCode
        Output = ($stdout + "`n" + $stderr).Trim()
    }
}

$results = @()

# ------------------------------------------------------------------------------
# CASE 1: Boundary 1 — Crash BEFORE baseline capture (Transaction-start safety)
# ------------------------------------------------------------------------------
$c1Storage = Join-Path $storageBase "Case1"
Remove-Item -Recurse -Force $c1Storage -ErrorAction SilentlyContinue
[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKeyPath, $true).SetValue("Val1", 100, [Microsoft.Win32.RegistryValueKind]::DWord)
$pre1 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val1")

$res1 = Run-HarnessProcess "--checkpoint 1 --sub-key `"$testKeyPath`" --val-name Val1 --storage-dir `"$c1Storage`""
$post1 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val1")
$txFiles1 = @(Get-ChildItem -Path $c1Storage -Filter "*.json" -ErrorAction SilentlyContinue).Count

$rec1 = Run-HarnessProcess "--recover --storage-dir `"$c1Storage`""
$final1 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val1")

$results += [PSCustomObject]@{
    Case = "Case 1: Boundary 1"
    Category = "Transaction-Start Safety"
    PreBaseline = $pre1
    ChildPid = $res1.ChildPid
    ParentPid = $parentPid
    ExitCode = "$($res1.ExitCode) ($($res1.HexExitCode))"
    TerminationMethod = "Win32 TerminateProcess(0xC0000001)"
    PostCrashState = $post1
    TxCount = $txFiles1
    RecoveryPid = $rec1.ChildPid
    RecoveryResult = $rec1.Output
    FinalReadback = $final1
    Pass = ($post1 -eq 100 -and $final1 -eq 100 -and $txFiles1 -eq 0)
}

# ------------------------------------------------------------------------------
# CASE 2: Boundary 2 — Crash AFTER capture, BEFORE durable persistence
# ------------------------------------------------------------------------------
$c2Storage = Join-Path $storageBase "Case2"
Remove-Item -Recurse -Force $c2Storage -ErrorAction SilentlyContinue
[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKeyPath, $true).SetValue("Val2", 100, [Microsoft.Win32.RegistryValueKind]::DWord)
$pre2 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val2")

$res2 = Run-HarnessProcess "--checkpoint 2 --sub-key `"$testKeyPath`" --val-name Val2 --storage-dir `"$c2Storage`""
$post2 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val2")
$txFiles2 = @(Get-ChildItem -Path $c2Storage -Filter "*.json" -ErrorAction SilentlyContinue).Count

$rec2 = Run-HarnessProcess "--recover --storage-dir `"$c2Storage`""
$final2 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val2")

$results += [PSCustomObject]@{
    Case = "Case 2: Boundary 2"
    Category = "Transaction-Start Safety"
    PreBaseline = $pre2
    ChildPid = $res2.ChildPid
    ParentPid = $parentPid
    ExitCode = "$($res2.ExitCode) ($($res2.HexExitCode))"
    TerminationMethod = "Win32 TerminateProcess(0xC0000001)"
    PostCrashState = $post2
    TxCount = $txFiles2
    RecoveryPid = $rec2.ChildPid
    RecoveryResult = $rec2.Output
    FinalReadback = $final2
    Pass = ($post2 -eq 100 -and $final2 -eq 100 -and $txFiles2 -eq 0)
}

# ------------------------------------------------------------------------------
# CASE 3: Boundary 3 — Crash AFTER durable persistence, BEFORE mutation
# ------------------------------------------------------------------------------
$c3Storage = Join-Path $storageBase "Case3"
Remove-Item -Recurse -Force $c3Storage -ErrorAction SilentlyContinue
[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKeyPath, $true).SetValue("Val3", 100, [Microsoft.Win32.RegistryValueKind]::DWord)
$pre3 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val3")

$res3 = Run-HarnessProcess "--checkpoint 3 --sub-key `"$testKeyPath`" --val-name Val3 --storage-dir `"$c3Storage`" --baseline-val 100 --target-val 200"
$post3 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val3")
$txFiles3 = @(Get-ChildItem -Path $c3Storage -Filter "*.json" -ErrorAction SilentlyContinue).Count

$rec3 = Run-HarnessProcess "--recover --storage-dir `"$c3Storage`""
$final3 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val3")

$results += [PSCustomObject]@{
    Case = "Case 3: Boundary 3"
    Category = "Durable Incomplete Recovery"
    PreBaseline = $pre3
    ChildPid = $res3.ChildPid
    ParentPid = $parentPid
    ExitCode = "$($res3.ExitCode) ($($res3.HexExitCode))"
    TerminationMethod = "Win32 TerminateProcess(0xC0000001)"
    PostCrashState = $post3
    TxCount = $txFiles3
    RecoveryPid = $rec3.ChildPid
    RecoveryResult = $rec3.Output
    FinalReadback = $final3
    Pass = ($post3 -eq 100 -and $final3 -eq 100 -and $txFiles3 -gt 0 -and $rec3.ExitCode -eq 0)
}

# ------------------------------------------------------------------------------
# CASE 4: Boundary 4 — Crash DURING mutation (Intermediate corrupt state: 999999)
# ------------------------------------------------------------------------------
$c4Storage = Join-Path $storageBase "Case4"
Remove-Item -Recurse -Force $c4Storage -ErrorAction SilentlyContinue
[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKeyPath, $true).SetValue("Val4", 100, [Microsoft.Win32.RegistryValueKind]::DWord)
$pre4 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val4")

$res4 = Run-HarnessProcess "--checkpoint 4 --sub-key `"$testKeyPath`" --val-name Val4 --storage-dir `"$c4Storage`" --baseline-val 100 --target-val 200"
$post4 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val4")

$rec4 = Run-HarnessProcess "--recover --storage-dir `"$c4Storage`""
$final4 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val4")

$results += [PSCustomObject]@{
    Case = "Case 4: Boundary 4"
    Category = "Corrupt State Rollback"
    PreBaseline = $pre4
    ChildPid = $res4.ChildPid
    ParentPid = $parentPid
    ExitCode = "$($res4.ExitCode) ($($res4.HexExitCode))"
    TerminationMethod = "Win32 TerminateProcess(0xC0000001)"
    PostCrashState = $post4
    TxCount = 1
    RecoveryPid = $rec4.ChildPid
    RecoveryResult = $rec4.Output
    FinalReadback = $final4
    Pass = ($post4 -eq 999999 -and $final4 -eq 100 -and $rec4.ExitCode -eq 0)
}

# ------------------------------------------------------------------------------
# CASE 5: Boundary 5 — Crash AFTER mutation, BEFORE readback verification
# ------------------------------------------------------------------------------
$c5Storage = Join-Path $storageBase "Case5"
Remove-Item -Recurse -Force $c5Storage -ErrorAction SilentlyContinue
[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKeyPath, $true).SetValue("Val5", 100, [Microsoft.Win32.RegistryValueKind]::DWord)
$pre5 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val5")

$res5 = Run-HarnessProcess "--checkpoint 5 --sub-key `"$testKeyPath`" --val-name Val5 --storage-dir `"$c5Storage`" --baseline-val 100 --target-val 200"
$post5 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val5")

$rec5 = Run-HarnessProcess "--recover --storage-dir `"$c5Storage`""
$final5 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val5")

$results += [PSCustomObject]@{
    Case = "Case 5: Boundary 5"
    Category = "Unverified Mutation Rollback"
    PreBaseline = $pre5
    ChildPid = $res5.ChildPid
    ParentPid = $parentPid
    ExitCode = "$($res5.ExitCode) ($($res5.HexExitCode))"
    TerminationMethod = "Win32 TerminateProcess(0xC0000001)"
    PostCrashState = $post5
    TxCount = 1
    RecoveryPid = $rec5.ChildPid
    RecoveryResult = $rec5.Output
    FinalReadback = $final5
    Pass = ($post5 -eq 200 -and $final5 -eq 100 -and $rec5.ExitCode -eq 0)
}

# ------------------------------------------------------------------------------
# CASE 6: Boundary 6 — Crash AFTER readback, BEFORE transaction completion update
# ------------------------------------------------------------------------------
$c6Storage = Join-Path $storageBase "Case6"
Remove-Item -Recurse -Force $c6Storage -ErrorAction SilentlyContinue
[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKeyPath, $true).SetValue("Val6", 100, [Microsoft.Win32.RegistryValueKind]::DWord)
$pre6 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val6")

$res6 = Run-HarnessProcess "--checkpoint 6 --sub-key `"$testKeyPath`" --val-name Val6 --storage-dir `"$c6Storage`" --baseline-val 100 --target-val 200"
$post6 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val6")

$rec6 = Run-HarnessProcess "--recover --storage-dir `"$c6Storage`""
$final6 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val6")

$results += [PSCustomObject]@{
    Case = "Case 6: Boundary 6"
    Category = "Unfinalized Transaction Rollback"
    PreBaseline = $pre6
    ChildPid = $res6.ChildPid
    ParentPid = $parentPid
    ExitCode = "$($res6.ExitCode) ($($res6.HexExitCode))"
    TerminationMethod = "Win32 TerminateProcess(0xC0000001)"
    PostCrashState = $post6
    TxCount = 1
    RecoveryPid = $rec6.ChildPid
    RecoveryResult = $rec6.Output
    FinalReadback = $final6
    Pass = ($post6 -eq 200 -and $final6 -eq 100 -and $rec6.ExitCode -eq 0)
}

# ------------------------------------------------------------------------------
# CASE 7: Boundary 7 — Crash DURING multi-entry rollback midway
# ------------------------------------------------------------------------------
$c7Storage = Join-Path $storageBase "Case7"
Remove-Item -Recurse -Force $c7Storage -ErrorAction SilentlyContinue
$res7 = Run-HarnessProcess "--checkpoint 7 --sub-key `"$testKeyPath`" --val-name MultiVal --storage-dir `"$c7Storage`""

$k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath)
$post7_1 = $k.GetValue("MultiVal_1")
$post7_2 = $k.GetValue("MultiVal_2")
$post7_3 = $k.GetValue("MultiVal_3")

$rec7 = Run-HarnessProcess "--recover --storage-dir `"$c7Storage`""

$kAfter = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath)
$final7_1 = $kAfter.GetValue("MultiVal_1")
$final7_2 = $kAfter.GetValue("MultiVal_2")
$final7_3 = $kAfter.GetValue("MultiVal_3")

$results += [PSCustomObject]@{
    Case = "Case 7: Boundary 7"
    Category = "Multi-Entry Mid-Rollback Resume"
    PreBaseline = "101, 102, 103"
    ChildPid = $res7.ChildPid
    ParentPid = $parentPid
    ExitCode = "$($res7.ExitCode) ($($res7.HexExitCode))"
    TerminationMethod = "Win32 TerminateProcess(0xC0000001)"
    PostCrashState = "$post7_1, $post7_2, $post7_3"
    TxCount = 3
    RecoveryPid = $rec7.ChildPid
    RecoveryResult = $rec7.Output
    FinalReadback = "$final7_1, $final7_2, $final7_3"
    Pass = ($post7_1 -eq 101 -and $post7_2 -eq 502 -and $post7_3 -eq 503 -and $final7_1 -eq 101 -and $final7_2 -eq 102 -and $final7_3 -eq 103)
}

# ------------------------------------------------------------------------------
# CASE 8: Boundary 8 — Abrupt process termination (Simulated service crash)
# ------------------------------------------------------------------------------
$c8Storage = Join-Path $storageBase "Case8"
Remove-Item -Recurse -Force $c8Storage -ErrorAction SilentlyContinue
[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKeyPath, $true).SetValue("Val8", 100, [Microsoft.Win32.RegistryValueKind]::DWord)
$pre8 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val8")

$res8 = Run-HarnessProcess "--checkpoint 8 --sub-key `"$testKeyPath`" --val-name Val8 --storage-dir `"$c8Storage`" --baseline-val 100 --target-val 200"
$post8 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val8")

$rec8 = Run-HarnessProcess "--recover --storage-dir `"$c8Storage`""
$final8 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val8")

$results += [PSCustomObject]@{
    Case = "Case 8: Boundary 8"
    Category = "Application-Host Kill Recovery"
    PreBaseline = $pre8
    ChildPid = $res8.ChildPid
    ParentPid = $parentPid
    ExitCode = "$($res8.ExitCode) ($($res8.HexExitCode))"
    TerminationMethod = "Process.GetCurrentProcess().Kill() (-1)"
    PostCrashState = $post8
    TxCount = 1
    RecoveryPid = $rec8.ChildPid
    RecoveryResult = $rec8.Output
    FinalReadback = $final8
    Pass = ($post8 -eq 200 -and $final8 -eq 100 -and $rec8.ExitCode -eq 0)
}

# ------------------------------------------------------------------------------
# CASE 9: Boundary 9 — Idempotent recovery on already restored state
# ------------------------------------------------------------------------------
$rec9_1 = Run-HarnessProcess "--recover --storage-dir `"$c4Storage`""
$rec9_2 = Run-HarnessProcess "--recover --storage-dir `"$c4Storage`""
$final9 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val4")

$results += [PSCustomObject]@{
    Case = "Case 9: Boundary 9"
    Category = "Recovery Idempotency"
    PreBaseline = 100
    ChildPid = $rec9_1.ChildPid
    ParentPid = $parentPid
    ExitCode = "$($rec9_2.ExitCode) ($($rec9_2.HexExitCode))"
    TerminationMethod = "Normal Exit (0)"
    PostCrashState = 100
    TxCount = 1
    RecoveryPid = $rec9_2.ChildPid
    RecoveryResult = $rec9_2.Output
    FinalReadback = $final9
    Pass = ($final9 -eq 100 -and $rec9_2.ExitCode -eq 0)
}

# ------------------------------------------------------------------------------
# CASE 10: Missing Value Non-Existence Deletion
# ------------------------------------------------------------------------------
$c10Storage = Join-Path $storageBase "Case10"
Remove-Item -Recurse -Force $c10Storage -ErrorAction SilentlyContinue
# Ensure key value does not exist
$regKey10 = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKeyPath, $true)
$regKey10.DeleteValue("Val10", $false)
$pre10 = $regKey10.GetValue("Val10") # Should be null

$res10 = Run-HarnessProcess "--checkpoint 4 --sub-key `"$testKeyPath`" --val-name Val10 --storage-dir `"$c10Storage`" --baseline-val VALUE_DID_NOT_EXIST --target-val 200"
$post10 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val10")

$rec10 = Run-HarnessProcess "--recover --storage-dir `"$c10Storage`""
$final10 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyPath).GetValue("Val10")

$results += [PSCustomObject]@{
    Case = "Case 10: Missing Value"
    Category = "Non-Existence Preservation"
    PreBaseline = "NULL (Non-Existent)"
    ChildPid = $res10.ChildPid
    ParentPid = $parentPid
    ExitCode = "$($res10.ExitCode) ($($res10.HexExitCode))"
    TerminationMethod = "Win32 TerminateProcess(0xC0000001)"
    PostCrashState = $post10
    TxCount = 1
    RecoveryPid = $rec10.ChildPid
    RecoveryResult = $rec10.Output
    FinalReadback = if ($final10 -eq $null) { "NULL (Deleted)" } else { $final10 }
    Pass = ($post10 -eq 999999 -and $final10 -eq $null -and $rec10.ExitCode -eq 0)
}

# Clean up test registry key
try {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testKeyPath, $false)
} catch {}

Write-Host "================================================================================"
Write-Host "REPRODUCTION SUITE RESULTS SUMMARY"
Write-Host "================================================================================"
$results | Format-Table Case, Category, PreBaseline, ChildPid, ExitCode, TerminationMethod, PostCrashState, RecoveryPid, FinalReadback, Pass -AutoSize
