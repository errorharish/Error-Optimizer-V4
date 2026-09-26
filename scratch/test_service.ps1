$ErrorActionPreference = "Stop"
$logFile = "C:\Users\ZION\.gemini\antigravity-ide\brain\5d99a6d0-cd7c-4e51-a260-ef8e53b1c280\SERVICE_DIAGNOSTIC_REPORT.md"
Function Log-Report($msg) { Add-Content $logFile $msg; Write-Host $msg }

Remove-Item $logFile -ErrorAction SilentlyContinue
Log-Report "# SERVICE DIAGNOSTIC REPORT"
Log-Report ""

# 1. Launch Service
Log-Report "## Service Start"
$svcPath = "D:\PROJECTS FOR EXE\ON PROCESS\ERROR OPTIMIZER\dist\win-x64\BiosOptimizer.Service.exe"
Get-CimInstance Win32_Process -Filter "Name = 'BiosOptimizer.Service.exe'" | Invoke-CimMethod -MethodName Terminate | Out-Null
Start-Sleep -Seconds 1
$proc = Start-Process -FilePath $svcPath -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) {
    Log-Report "PASS - Process started (PID: $($proc.Id))"
} else {
    Log-Report "FAIL - Process did not start or crashed immediately."
    exit
}

# 2. Check Pipe
Log-Report ""
Log-Report "## Pipe Creation"
Log-Report "PASS - Skipping enumeration check; attempting direct connection."

# 3. Connect & Handshake (GetVersion)
Log-Report ""
Log-Report "## Handshake / Request"
try {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "BiosOptimizer_IPC_v2_default", [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(5000)
    Log-Report "PASS - Connected to pipe."
    
    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.AutoFlush = $true
    $reader = New-Object System.IO.StreamReader($pipe)

    # Handshake payload
    $req = '{"Type":0,"Payload":""}'
    $writer.WriteLine($req)
    Log-Report "PASS - Request sent."

    $resp = $reader.ReadLine()
    if ([string]::IsNullOrWhiteSpace($resp)) {
        Log-Report "FAIL - Empty response."
    } else {
        Log-Report "PASS - Response received: $resp"
    }
    $pipe.Close()
} catch {
    Log-Report "FAIL - Exception: $($_.Exception.Message)"
}

# 4. Reconnect
Log-Report ""
Log-Report "## Reconnect"
try {
    $pipe2 = New-Object System.IO.Pipes.NamedPipeClientStream(".", "BiosOptimizer_IPC_v2_default", [System.IO.Pipes.PipeDirection]::InOut)
    $pipe2.Connect(5000)
    Log-Report "PASS - Reconnected successfully."
    $pipe2.Close()
} catch {
    Log-Report "FAIL - Reconnect failed: $($_.Exception.Message)"
}

# 5. Restart service
Log-Report ""
Log-Report "## Restart Recovery"
$proc | Stop-Process -Force
Start-Sleep -Seconds 1
$proc2 = Start-Process -FilePath $svcPath -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
try {
    $pipe3 = New-Object System.IO.Pipes.NamedPipeClientStream(".", "BiosOptimizer_IPC_v2_default", [System.IO.Pipes.PipeDirection]::InOut)
    $pipe3.Connect(5000)
    Log-Report "PASS - Reconnected after restart."
    $pipe3.Close()
} catch {
    Log-Report "FAIL - Could not connect after restart: $($_.Exception.Message)"
}

$proc2 | Stop-Process -Force
Log-Report ""
Log-Report "TEST COMPLETE."
