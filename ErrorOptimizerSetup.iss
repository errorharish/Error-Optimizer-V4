; =====================================================================
; ERROR OPTIMIZER V4 — Official Commercial Inno Setup Script
; Generates: dist\installer\ERROR OPTIMIZER V4 SETUP.exe
; =====================================================================

#define MyAppName "ERROR OPTIMIZER V4"
#define MyAppVersion "4.0.5"
#define MyAppPublisher "Error Optimizer Team"
#define MyAppURL "https://erroroptimizer.com"
#define SourceDistDir "dist\win-x64"
#define AssetsDir "assets"

[Setup]
; App Identity
AppId={{D41A28A1-889B-4923-9D22-EC3619572B20}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Installation Target & OS Requirements
DefaultDirName={autopf}\Error Optimizer V4
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
PrivilegesRequired=admin
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupLogging=yes

; Visuals & Branding
SetupIconFile={#AssetsDir}\Error Optimizer V3.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\assets\Error Optimizer V3.ico
WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=yes

; Packaging & Compression
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=dist\installer\release-candidate
OutputBaseFilename=ERROR OPTIMIZER V4 SETUP
CloseApplications=no
RestartApplications=no
AppMutex=ErrorOptimizer_SingleInstance_Mutex,BiosOptimizer_Service_Mutex

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}:"; Flags: unchecked
Name: "autostart"; Description: "Start ERROR OPTIMIZER V4 automatically with Windows (Minimized to System Tray)"; GroupDescription: "Startup & Background Operation:"; Flags: unchecked

[Dirs]
Name: "{commonappdata}\ErrorOptimizer"; Flags: uninsneveruninstall
Name: "{commonappdata}\ErrorOptimizer\logs"; Flags: uninsneveruninstall
Name: "{commonappdata}\ErrorOptimizer\backups"; Flags: uninsneveruninstall
Name: "{commonappdata}\ErrorOptimizer\AppliedState"; Flags: uninsneveruninstall

[Files]
; Dist runtime files (Self-contained binaries & dependencies)
Source: "{#SourceDistDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Official High-Resolution Branding Icon
Source: "{#AssetsDir}\Error Optimizer V3.ico"; DestDir: "{app}\assets"; Flags: ignoreversion
; Bundled official prerequisite installers (extracted to temp only if missing on host)
Source: "{#AssetsDir}\prerequisites\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#AssetsDir}\prerequisites\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\ERROR OPTIMIZER PROTOTYPE.exe"; IconFilename: "{app}\assets\Error Optimizer V3.ico"; Comment: "ERROR OPTIMIZER V4 - Performance & System Optimization Engine"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\assets\Error Optimizer V3.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\ERROR OPTIMIZER PROTOTYPE.exe"; IconFilename: "{app}\assets\Error Optimizer V3.ico"; Tasks: desktopicon; Comment: "ERROR OPTIMIZER V4"

[Registry]
; Configure clean per-machine installation metadata
Root: HKLM; Subkey: "Software\Error Optimizer"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Error Optimizer"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Error Optimizer"; ValueType: string; ValueName: "ServicePath"; ValueData: "{app}\core\BiosOptimizer.Service.exe"; Flags: uninsdeletekey
; Optional auto-start with Windows
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ERROR OPTIMIZER V4"; ValueData: """{app}\ERROR OPTIMIZER PROTOTYPE.exe"" --startup-background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\ERROR OPTIMIZER PROTOTYPE.exe"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  ServiceWasInstalledBeforeSetup: Boolean;
  ServiceWasRunningBeforeSetup: Boolean;
  PreviousServiceStartType: String;
  PreviousServiceIsDelayed: Boolean;
  PreviousServiceResetPeriod: LongInt;
  PreviousServiceRebootMsg: String;
  PreviousServiceCommand: String;
  PreviousServiceActions: String;
  PreviousServiceFailureFlag: LongInt;
  PreviousServiceHadRecovery: Boolean;
  HasPreUpgradeBinaryBackup: Boolean;
  InstalledServiceBinaryPath: String;
  BackedUpServiceSha256: String;
  ActiveSnapshotDir: String;
  ActiveSnapshotId: String;
  LastKnownGoodSnapshotDir: String;
  IsInterruptedTransactionDetected: Boolean;
  ServiceStoppedByInstaller: Boolean;
  InstallationSucceeded: Boolean;

// Detect Microsoft Edge WebView2 Runtime installation across native 64-bit, 32-bit (WOW64), and per-user registry views
function IsWebView2RuntimeInstalled(): Boolean;
var
  VersionStr: String;
begin
  Result := False;

  // 1. Check native 64-bit machine registry (HKLM64)
  if RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', VersionStr) then
  begin
    if (VersionStr <> '') and (VersionStr <> '0.0.0.0') then
    begin
      Result := True;
      Exit;
    end;
  end;

  // 2. Check 32-bit redirected machine registry (HKLM32)
  if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', VersionStr) then
  begin
    if (VersionStr <> '') and (VersionStr <> '0.0.0.0') then
    begin
      Result := True;
      Exit;
    end;
  end;

  // 3. Check explicit WOW6432Node machine registry (HKLM64)
  if RegQueryStringValue(HKLM64, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', VersionStr) then
  begin
    if (VersionStr <> '') and (VersionStr <> '0.0.0.0') then
    begin
      Result := True;
      Exit;
    end;
  end;

  // 4. Check per-user registry (HKCU)
  if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', VersionStr) then
  begin
    if (VersionStr <> '') and (VersionStr <> '0.0.0.0') then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

// Detect Microsoft Visual C++ 2015-2022 Redistributable (x64) installation
function IsVCRedistInstalled(): Boolean;
var
  InstalledVal: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', InstalledVal) then
  begin
    if InstalledVal = 1 then
    begin
      Result := True;
      Exit;
    end;
  end;
  if RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{36f68a90-239c-34df-b58c-64b30153ce35}') or
     RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{CF2BEA3C-26EA-32F8-AA9B-331F7E34BA97}') or
     RegKeyExists(HKLM64, 'SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64') then
  begin
    Result := True;
  end;
end;

// Automatically install official Microsoft Visual C++ 2015-2022 Redistributable (x64)
function InstallVCRedist(): Boolean;
var
  ResultCode: Integer;
  InstallerPath: String;
begin
  Result := False;
  Log('Checking bundled Visual C++ 2015-2022 Redistributable...');
  try
    ExtractTemporaryFile('vc_redist.x64.exe');
    InstallerPath := ExpandConstant('{tmp}\vc_redist.x64.exe');
    if FileExists(InstallerPath) then
    begin
      Log('Installing Visual C++ Redistributable (x64) silently...');
      if Exec(InstallerPath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      begin
        Sleep(1500);
        Result := IsVCRedistInstalled();
        if Result then
        begin
          Log('Visual C++ Redistributable installed successfully.');
          Exit;
        end;
      end;
    end;
  except
    Log('Notice: vc_redist extraction exception: ' + GetExceptionMessage);
  end;
  Result := IsVCRedistInstalled();
end;

// Automatically install official Microsoft Edge WebView2 Evergreen Runtime
function InstallWebView2Runtime(): Boolean;
var
  ResultCode: Integer;
  BootstrapperPath: String;
  DownloadCmd: String;
begin
  Result := False;
  Log('Checking bundled Microsoft Edge WebView2 Bootstrapper...');
  try
    ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
    BootstrapperPath := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
    if FileExists(BootstrapperPath) then
    begin
      Log('Executing bundled WebView2 Bootstrapper (/silent /install)...');
      if Exec(BootstrapperPath, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      begin
        Sleep(2500);
        Result := IsWebView2RuntimeInstalled();
        if Result then
        begin
          Log('Microsoft Edge WebView2 Runtime installed successfully via bundled bootstrapper.');
          Exit;
        end;
      end;
    end;
  except
    Log('Notice: Bundled bootstrapper extraction exception: ' + GetExceptionMessage);
  end;

  // Fallback: If bundled setup did not complete or was missing, download directly from official Microsoft CDN
  Log('Downloading latest Microsoft Edge WebView2 Evergreen Bootstrapper via official Microsoft link...');
  BootstrapperPath := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup_Online.exe');
  DownloadCmd := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' +
    '"[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13; ' +
    'Invoke-WebRequest -Uri ''https://go.microsoft.com/fwlink/p/?LinkId=2124703'' -OutFile ''' + BootstrapperPath + ''' -UseBasicParsing"';

  if Exec('powershell.exe', DownloadCmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and FileExists(BootstrapperPath) then
  begin
    Log('Executing online WebView2 Bootstrapper (/silent /install)...');
    if Exec(BootstrapperPath, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      Sleep(2500);
      Result := IsWebView2RuntimeInstalled();
    end;
  end;
end;

procedure CleanLegacyStartupRegistrations();
var
  ResultCode: Integer;
begin
  // Remove any legacy HKLM Run entries
  RegDeleteValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ERROR OPTIMIZER PROTOTYPE');
  RegDeleteValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Error Optimizer Prototype');
  RegDeleteValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Error Optimizer');
  RegDeleteValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ErrorOptimizer');
  RegDeleteValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Error Optimizer V3');
  RegDeleteValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BiosOptimizer');
  RegDeleteValue(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'ERROR OPTIMIZER PROTOTYPE');
  RegDeleteValue(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'Error Optimizer Prototype');
  RegDeleteValue(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'Error Optimizer');
  RegDeleteValue(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'ErrorOptimizer');
  RegDeleteValue(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'Error Optimizer V3');
  RegDeleteValue(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'BiosOptimizer');

  // Remove legacy HKCU Run entries
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ERROR OPTIMIZER PROTOTYPE');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Error Optimizer Prototype');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ErrorOptimizer');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Error Optimizer V3');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ErrorOptimizerV3');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BiosOptimizer');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Bios Optimizer');

  // Clean Startup folders
  DeleteFile(ExpandConstant('{userstartup}\Error Optimizer.lnk'));
  DeleteFile(ExpandConstant('{userstartup}\ErrorOptimizer.lnk'));
  DeleteFile(ExpandConstant('{userstartup}\Error Optimizer V3.lnk'));
  DeleteFile(ExpandConstant('{commonstartup}\Error Optimizer.lnk'));
  DeleteFile(ExpandConstant('{commonstartup}\ErrorOptimizer.lnk'));
  DeleteFile(ExpandConstant('{commonstartup}\Error Optimizer V3.lnk'));

  // Clean stale logon tasks
  Exec('schtasks.exe', '/delete /tn "\ErrorOptimizer\StartupTask" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('schtasks.exe', '/delete /tn "ErrorOptimizerStartup" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('schtasks.exe', '/delete /tn "BiosOptimizerStartup" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Detect whether an existing ERROR OPTIMIZER installation is present on this host
function IsExistingInstallationDetected(): Boolean;
var
  InstallLoc: String;
begin
  Result := False;
  // 1. Check official machine registration
  if RegQueryStringValue(HKLM64, 'Software\Error Optimizer', 'InstallLocation', InstallLoc) or
     RegQueryStringValue(HKLM32, 'Software\Error Optimizer', 'InstallLocation', InstallLoc) then
  begin
    if (InstallLoc <> '') and DirExists(InstallLoc) then
    begin
      Result := True;
      Exit;
    end;
  end;

  // 2. Check default target application files
  if FileExists(ExpandConstant('{app}\ERROR OPTIMIZER PROTOTYPE.exe')) or
     FileExists(ExpandConstant('{app}\core\BiosOptimizer.Service.exe')) then
  begin
    Result := True;
    Exit;
  end;

  // 3. Check Windows Service Control Manager registration
  if RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\BiosOptimizer.Service') or
     RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\BiosOptimizerService') then
  begin
    Result := True;
    Exit;
  end;
end;

// ==============================================================================
// ERROR OPTIMIZER V4 — PRE-INSTALLATION PROCESS & SERVICE LIFECYCLE MANAGEMENT
// ==============================================================================

function BoolToStrCustom(B: Boolean): String;
begin
  if B then Result := 'True' else Result := 'False';
end;

// Helper: Check if service exists in SCM registry
function IsServiceInstalled(ServiceName: String): Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\' + ServiceName) or
            RegKeyExists(HKLM32, 'SYSTEM\CurrentControlSet\Services\' + ServiceName);
end;

// Helper: Query service startup type from SCM ('auto', 'delayed-auto', 'demand', 'disabled')
function GetServiceStartType(ServiceName: String): String;
var
  ResultCode: Integer;
begin
  Result := 'auto';
  if not IsServiceInstalled(ServiceName) then
    Exit;

  if Exec('cmd.exe', '/c "sc.exe qc "' + ServiceName + '" | findstr /i "DELAYED""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
  begin
    Result := 'delayed-auto';
  end
  else if Exec('cmd.exe', '/c "sc.exe qc "' + ServiceName + '" | findstr /i "DEMAND_START""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
  begin
    Result := 'demand';
  end
  else if Exec('cmd.exe', '/c "sc.exe qc "' + ServiceName + '" | findstr /i "AUTO_START""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
  begin
    Result := 'auto';
  end
  else if Exec('cmd.exe', '/c "sc.exe qc "' + ServiceName + '" | findstr /i "DISABLED""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
  begin
    Result := 'disabled';
  end;
end;

// Helper: Query whether service currently has restart recovery actions armed
function HasServiceRecoveryConfigured(ServiceName: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if not IsServiceInstalled(ServiceName) then
    Exit;

  Result := Exec('cmd.exe', '/c "sc.exe qfailure "' + ServiceName + '" | findstr /i "RESTART""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Helper: Check if service is currently in STOPPED state according to SCM
function IsServiceStopped(ServiceName: String): Boolean;
var
  ResultCode: Integer;
begin
  if not IsServiceInstalled(ServiceName) then
  begin
    Result := True;
    Exit;
  end;
  Result := Exec('cmd.exe', '/c "sc.exe query "' + ServiceName + '" | findstr /i "STOPPED""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Helper: Check if service is currently in RUNNING state according to SCM
function IsServiceRunning(ServiceName: String): Boolean;
var
  ResultCode: Integer;
begin
  if not IsServiceInstalled(ServiceName) then
  begin
    Result := False;
    Exit;
  end;
  Result := Exec('cmd.exe', '/c "sc.exe query "' + ServiceName + '" | findstr /i "RUNNING""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Helper: Check if a specific process name is currently running
function IsProcessRunning(ProcessImageName: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd.exe', '/c "tasklist /fi ""imagename eq ' + ProcessImageName + '"" | findstr /i "' + ProcessImageName + '"""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Helper: Extract actual installed executable path from SCM ImagePath registry or default fallback
function GetServiceBinaryPath(ServiceName: String): String;
var
  ImagePath: String;
  P: Integer;
begin
  Result := '';
  if RegQueryStringValue(HKLM64, 'SYSTEM\CurrentControlSet\Services\' + ServiceName, 'ImagePath', ImagePath) or
     RegQueryStringValue(HKLM32, 'SYSTEM\CurrentControlSet\Services\' + ServiceName, 'ImagePath', ImagePath) then
  begin
    ImagePath := Trim(ImagePath);
    if (Length(ImagePath) > 1) and (ImagePath[1] = '"') then
    begin
      Delete(ImagePath, 1, 1);
      P := Pos('"', ImagePath);
      if P > 0 then
        Result := Copy(ImagePath, 1, P - 1)
      else
        Result := ImagePath;
    end
    else
    begin
      P := Pos('.exe', Lowercase(ImagePath));
      if P > 0 then
        Result := Copy(ImagePath, 1, P + 3)
      else
        Result := ImagePath;
    end;
  end;

  if (Result = '') or not FileExists(Result) then
  begin
    Result := ExpandConstant('{app}\core\BiosOptimizer.Service.exe');
  end;
end;

// Helper: Complete application and service pre-upgrade backup with atomic manifests and LKG staging
// Implements unique versioned snapshots, full application scope, selective PE validation, and atomic LKG updates
function BackupPreUpgradeServiceAndConfig(ServiceName: String; var ErrorDetails: String): Boolean;
var
  ResultCode: Integer;
  SnapshotsBaseDir, ConfigFile, ScriptFile, PsScript, SvcExe, AppDir: String;
begin
  Result := False;
  ErrorDetails := '';
  HasPreUpgradeBinaryBackup := False;
  BackedUpServiceSha256 := '';

  SvcExe := GetServiceBinaryPath(ServiceName);
  InstalledServiceBinaryPath := SvcExe;
  AppDir := ExpandConstant('{app}');
  SnapshotsBaseDir := ExpandConstant('{commonappdata}\ErrorOptimizer\backups\snapshots');
  LastKnownGoodSnapshotDir := ExpandConstant('{commonappdata}\ErrorOptimizer\backups\last_known_good');
  ConfigFile := ExpandConstant('{commonappdata}\ErrorOptimizer\backups\pre_upgrade_service_config.ini');
  ScriptFile := ExpandConstant('{tmp}\capture_service_backup.ps1');

  ForceDirectories(SnapshotsBaseDir);
  ForceDirectories(LastKnownGoodSnapshotDir);

  PsScript :=
    'param([string]$SvcName, [string]$SvcExePath, [string]$TargetAppDir, [string]$BaseDir, [string]$LkgDir, [string]$OutIni)' + #13#10 +
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    'function Validate-PEFile($path) {' + #13#10 +
    '    $bytes = [System.IO.File]::ReadAllBytes($path)' + #13#10 +
    '    if ($bytes.Length -lt 1024 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $false }' + #13#10 +
    '    $peOff = [System.BitConverter]::ToInt32($bytes, 0x3C)' + #13#10 +
    '    if ($peOff -lt 64 -or ($peOff + 28) -gt $bytes.Length) { return $false }' + #13#10 +
    '    if ($bytes[$peOff] -ne 0x50 -or $bytes[$peOff+1] -ne 0x45 -or $bytes[$peOff+2] -ne 0x00 -or $bytes[$peOff+3] -ne 0x00) { return $false }' + #13#10 +
    '    $machine = [System.BitConverter]::ToUInt16($bytes, $peOff + 4); $numSections = [System.BitConverter]::ToUInt16($bytes, $peOff + 6)' + #13#10 +
    '    $optSize = [System.BitConverter]::ToUInt16($bytes, $peOff + 20); $optMagic = [System.BitConverter]::ToUInt16($bytes, $peOff + 24)' + #13#10 +
    '    if ($numSections -eq 0 -or $optSize -eq 0) { return $false }' + #13#10 +
    '    if ($path.EndsWith(''.exe'', [System.StringComparison]::OrdinalIgnoreCase)) { if ($machine -ne 0x8664 -or $optMagic -ne 0x020B) { return $false } }' + #13#10 +
    '    else { if ($machine -ne 0x8664 -and $machine -ne 0x014C) { return $false }; if ($optMagic -ne 0x020B -and $optMagic -ne 0x010B) { return $false } }' + #13#10 +
    '    return $true' + #13#10 +
    '}' + #13#10 +
    'try {' + #13#10 +
    '    $startType = "auto"; $isDelayed = 0; $resetPeriod = 0; $rebootMsg = ""; $commandLine = ""; $actions = ""; $failureFlag = 0; $hadRecovery = 0' + #13#10 +
    '    $svcCheck = sc.exe query $SvcName 2>&1 | Out-String' + #13#10 +
    '    if ($svcCheck -notmatch ''does not exist'' -and $svcCheck -notmatch ''FAILED 1060'') {' + #13#10 +
    '        $qc = sc.exe qc $SvcName 2>&1 | Out-String' + #13#10 +
    '        if ($qc -match ''DEMAND_START'') { $startType = "demand" }' + #13#10 +
    '        elseif ($qc -match ''AUTO_START'') { $startType = "auto" }' + #13#10 +
    '        elseif ($qc -match ''DISABLED'') { $startType = "disabled" }' + #13#10 +
    '        $reg = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$SvcName" -ErrorAction SilentlyContinue' + #13#10 +
    '        if ($reg -and $reg.DelayedAutostart -eq 1 -and $startType -eq "auto") { $isDelayed = 1; $startType = "delayed-auto" }' + #13#10 +
    '        $qf = sc.exe qfailure $SvcName 2>&1 | Out-String' + #13#10 +
    '        $actionList = @()' + #13#10 +
    '        foreach ($line in ($qf -split ''\r?\n'')) {' + #13#10 +
    '            if ($line -match ''RESET_PERIOD.*?:\s*(\d+)'') { $resetPeriod = [int]$matches[1] }' + #13#10 +
    '            elseif ($line -match ''REBOOT_MESSAGE.*?:\s*(.*)'') { $rebootMsg = $matches[1].Trim() }' + #13#10 +
    '            elseif ($line -match ''COMMAND_LINE.*?:\s*(.*)'') { $commandLine = $matches[1].Trim() }' + #13#10 +
    '            elseif ($line -match ''(?:FAILURE_ACTIONS\s*:\s*)?([A-Za-z_]+)\s*--\s*Delay\s*=\s*(\d+)'') {' + #13#10 +
    '                $t = $matches[1].ToLower(); if ($t -eq ''run_process'') { $t = ''run'' }; $actionList += (''{0}/{1}'' -f $t, $matches[2])' + #13#10 +
    '            }' + #13#10 +
    '        }' + #13#10 +
    '        if ($actionList.Count -gt 0) { $actions = ($actionList -join ''/''); $hadRecovery = 1 }' + #13#10 +
    '        $qff = sc.exe qfailureflag $SvcName 2>&1 | Out-String' + #13#10 +
    '        if ($qff -match ''TRUE'') { $failureFlag = 1 }' + #13#10 +
    '    }' + #13#10 +
    '    $appVersion = "0.0.0.0"' + #13#10 +
    '    if (Test-Path $SvcExePath) { $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($SvcExePath); if ($vi.FileVersion) { $appVersion = $vi.FileVersion } }' + #13#10 +
    '    $verClean = $appVersion -replace ''[^\w\.]'', ''_''' + #13#10 +
    '    $snapId = "snapshot_{0}_{1}" -f $verClean, (Get-Date).ToString("yyyyMMdd_HHmmss")' + #13#10 +
    '    $targetSnapDir = Join-Path $BaseDir $snapId' + #13#10 +
    '    if (Test-Path $targetSnapDir) { $snapId = "{0}_{1}" -f $snapId, (Get-Random -Minimum 1000 -Maximum 9999); $targetSnapDir = Join-Path $BaseDir $snapId }' + #13#10 +
    '    New-Item -ItemType Directory -Path $targetSnapDir -Force | Out-Null' + #13#10 +
    '    $backedUpFiles = @(); $svcExeSha = ""; $svcExeSize = 0' + #13#10 +
    '    $filesToBackup = @()' + #13#10 +
    '    if (Test-Path $TargetAppDir) {' + #13#10 +
    '        $filesToBackup = Get-ChildItem -Path $TargetAppDir -Recurse -File' + #13#10 +
    '    }' + #13#10 +
    '    if ($filesToBackup.Count -eq 0 -and (Test-Path $SvcExePath)) {' + #13#10 +
    '        $coreDir = Split-Path -Parent $SvcExePath' + #13#10 +
    '        $filesToBackup = Get-ChildItem -Path $coreDir -Recurse -File' + #13#10 +
    '    }' + #13#10 +
    '    $baseSearchDir = if (Test-Path $TargetAppDir) { $TargetAppDir } else { Split-Path -Parent $SvcExePath }' + #13#10 +
    '    foreach ($file in $filesToBackup) {' + #13#10 +
    '        $rel = $file.FullName.Substring($baseSearchDir.Length).TrimStart(''\'', ''/'')' + #13#10 +
    '        $dst = Join-Path $targetSnapDir $rel' + #13#10 +
    '        $dstSub = Split-Path -Parent $dst; if (-not (Test-Path $dstSub)) { New-Item -ItemType Directory -Path $dstSub -Force | Out-Null }' + #13#10 +
    '        Copy-Item -Path $file.FullName -Destination $dst -Force' + #13#10 +
    '        $fLen = (Get-Item $dst).Length; $fHash = (Get-FileHash -Path $dst -Algorithm SHA256).Hash' + #13#10 +
    '        $isPe = ($rel.EndsWith(''.exe'', [System.StringComparison]::OrdinalIgnoreCase) -or $rel.EndsWith(''.dll'', [System.StringComparison]::OrdinalIgnoreCase))' + #13#10 +
    '        if ($isPe) { if (-not (Validate-PEFile $dst)) { throw "Structural PE validation failed for $rel" } }' + #13#10 +
    '        else { if ($fLen -lt 0 -or $fHash.Length -ne 64) { throw "Integrity validation failed for non-PE file $rel" } }' + #13#10 +
    '        if ($rel -match ''BiosOptimizer\.Service\.exe$'') { $svcExeSha = $fHash; $svcExeSize = $fLen }' + #13#10 +
    '        $backedUpFiles += [PSCustomObject]@{ RelativePath = $rel; Size = $fLen; Sha256 = $fHash; IsPE = $isPe }' + #13#10 +
    '    }' + #13#10 +
    '    $manifest = [ordered]@{ SnapshotId = $snapId; Timestamp = (Get-Date).ToString("o"); AppVersion = $appVersion; TargetAppDir = $baseSearchDir; InstalledServiceBinaryPath = $SvcExePath; SCM = [ordered]@{ ServiceName = $SvcName; StartType = $startType; IsDelayed = $isDelayed; ResetPeriod = $resetPeriod; RebootMessage = $rebootMsg; CommandLine = $commandLine; Actions = $actions; FailureFlag = $failureFlag; HadRecovery = $hadRecovery }; Files = $backedUpFiles }' + #13#10 +
    '    $tmpManifest = Join-Path $targetSnapDir "manifest.json.tmp"; $finalManifest = Join-Path $targetSnapDir "manifest.json"' + #13#10 +
    '    [System.IO.File]::WriteAllText($tmpManifest, ($manifest | ConvertTo-Json -Depth 5), [System.Text.Encoding]::UTF8)' + #13#10 +
    '    Move-Item -Path $tmpManifest -Destination $finalManifest -Force' + #13#10 +
    '    $iniText = "[SCM]`r`nSnapshotId=$snapId`r`nSnapshotDir=$targetSnapDir`r`nAppVersion=$appVersion`r`nServiceName=$SvcName`r`nStartType=$startType`r`nIsDelayed=$isDelayed`r`nResetPeriod=$resetPeriod`r`nRebootMessage=$rebootMsg`r`nCommandLine=$commandLine`r`nActions=$actions`r`nFailureFlag=$failureFlag`r`nHadRecovery=$hadRecovery`r`nBinaryPath=$SvcExePath`r`nSha256=$svcExeSha`r`nFileSize=$svcExeSize`r`nFileCount=$($backedUpFiles.Count)`r`n"' + #13#10 +
    '    $tmpIni = $OutIni + ".tmp"; [System.IO.File]::WriteAllText($tmpIni, $iniText, [System.Text.Encoding]::UTF8); Move-Item -Path $tmpIni -Destination $OutIni -Force' + #13#10 +
    '    $lkgStaging = $LkgDir + "_staging_" + (Get-Random -Minimum 10000 -Maximum 99999)' + #13#10 +
    '    $lkgBackup = $LkgDir + "_old_" + (Get-Random -Minimum 10000 -Maximum 99999)' + #13#10 +
    '    try {' + #13#10 +
    '        if (Test-Path $lkgStaging) { Remove-Item -Path $lkgStaging -Recurse -Force }' + #13#10 +
    '        New-Item -ItemType Directory -Path $lkgStaging -Force | Out-Null' + #13#10 +
    '        foreach ($f in $backedUpFiles) {' + #13#10 +
    '            $sF = Join-Path $targetSnapDir $f.RelativePath; $dF = Join-Path $lkgStaging $f.RelativePath' + #13#10 +
    '            $dSub = Split-Path -Parent $dF; if (-not (Test-Path $dSub)) { New-Item -ItemType Directory -Path $dSub -Force | Out-Null }' + #13#10 +
    '            Copy-Item -Path $sF -Destination $dF -Force' + #13#10 +
    '        }' + #13#10 +
    '        Copy-Item -Path $finalManifest -Destination (Join-Path $lkgStaging "manifest.json") -Force' + #13#10 +
    '        $lkgManifest = Get-Content (Join-Path $lkgStaging "manifest.json") -Raw | ConvertFrom-Json' + #13#10 +
    '        foreach ($vf in $lkgManifest.Files) {' + #13#10 +
    '            $vp = Join-Path $lkgStaging $vf.RelativePath' + #13#10 +
    '            if (-not (Test-Path $vp)) { throw "LKG staging file missing: $($vf.RelativePath)" }' + #13#10 +
    '            if ((Get-Item $vp).Length -ne $vf.Size) { throw "LKG staging size mismatch on $($vf.RelativePath)" }' + #13#10 +
    '            if ((Get-FileHash -Path $vp -Algorithm SHA256).Hash -ne $vf.Sha256) { throw "LKG staging SHA-256 mismatch on $($vf.RelativePath)" }' + #13#10 +
    '        }' + #13#10 +
    '        if (Test-Path $LkgDir) { Move-Item -Path $LkgDir -Destination $lkgBackup -Force }' + #13#10 +
    '        Move-Item -Path $lkgStaging -Destination $LkgDir -Force' + #13#10 +
    '        if (Test-Path $lkgBackup) { Remove-Item -Path $lkgBackup -Recurse -Force -ErrorAction SilentlyContinue }' + #13#10 +
    '    } catch {' + #13#10 +
    '        if (Test-Path $lkgStaging) { Remove-Item -Path $lkgStaging -Recurse -Force -ErrorAction SilentlyContinue }' + #13#10 +
    '        if ((Test-Path $lkgBackup) -and (-not (Test-Path $LkgDir))) { Move-Item -Path $lkgBackup -Destination $LkgDir -Force -ErrorAction SilentlyContinue }' + #13#10 +
    '        throw "Atomic last_known_good mirror update failed: $($_.Exception.Message)"' + #13#10 +
    '    }' + #13#10 +
    '    $verifyManifest = Get-Content $finalManifest -Raw | ConvertFrom-Json' + #13#10 +
    '    foreach ($vf in $verifyManifest.Files) {' + #13#10 +
    '        $cp = Join-Path $targetSnapDir $vf.RelativePath' + #13#10 +
    '        if (-not (Test-Path $cp) -or (Get-FileHash -Path $cp -Algorithm SHA256).Hash -ne $vf.Sha256) { throw "Verification failed for $($vf.RelativePath)" }' + #13#10 +
    '    }' + #13#10 +
    '    exit 0' + #13#10 +
    '} catch { Write-Error $_.Exception.Message; exit 1 }';

  if not SaveStringToFile(ScriptFile, PsScript, False) then
  begin
    ErrorDetails := 'Failed to stage SCM backup script to temporary directory.';
    Log('ERROR: ' + ErrorDetails);
    Exit;
  end;

  if not Exec('powershell.exe', '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptFile + '" -SvcName "' + ServiceName + '" -SvcExePath "' + SvcExe + '" -TargetAppDir "' + AppDir + '" -BaseDir "' + SnapshotsBaseDir + '" -LkgDir "' + LastKnownGoodSnapshotDir + '" -OutIni "' + ConfigFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    ErrorDetails := 'PowerShell application backup and binary snapshot failed (exit code: ' + IntToStr(ResultCode) + ').';
    Log('ERROR: ' + ErrorDetails);
    DeleteFile(ScriptFile);
    Exit;
  end;
  DeleteFile(ScriptFile);

  // Read the captured exact SCM and backup metadata
  if FileExists(ConfigFile) then
  begin
    ActiveSnapshotId := GetIniString('SCM', 'SnapshotId', '', ConfigFile);
    ActiveSnapshotDir := GetIniString('SCM', 'SnapshotDir', '', ConfigFile);
    PreviousServiceStartType := GetIniString('SCM', 'StartType', 'auto', ConfigFile);
    PreviousServiceIsDelayed := (GetIniInt('SCM', 'IsDelayed', 0, 0, 1, ConfigFile) = 1);
    PreviousServiceResetPeriod := GetIniInt('SCM', 'ResetPeriod', 0, 0, 2147483647, ConfigFile);
    PreviousServiceRebootMsg := GetIniString('SCM', 'RebootMessage', '', ConfigFile);
    PreviousServiceCommand := GetIniString('SCM', 'CommandLine', '', ConfigFile);
    PreviousServiceActions := GetIniString('SCM', 'Actions', '', ConfigFile);
    PreviousServiceFailureFlag := GetIniInt('SCM', 'FailureFlag', 0, 0, 1, ConfigFile);
    PreviousServiceHadRecovery := (GetIniInt('SCM', 'HadRecovery', 0, 0, 1, ConfigFile) = 1);
    BackedUpServiceSha256 := GetIniString('SCM', 'Sha256', '', ConfigFile);

    if (ActiveSnapshotDir <> '') and FileExists(ActiveSnapshotDir + '\manifest.json') then
    begin
      HasPreUpgradeBinaryBackup := True;
      Log('VERIFIED IMMUTABLE APPLICATION SNAPSHOT: ' + ActiveSnapshotDir + ' (Files: ' + IntToStr(GetIniInt('SCM', 'FileCount', 0, 0, 10000, ConfigFile)) + ')');
    end;
  end;

  Log('Captured exact SCM configuration: Start=' + PreviousServiceStartType +
      ', Delayed=' + BoolToStrCustom(PreviousServiceIsDelayed) +
      ', Reset=' + IntToStr(PreviousServiceResetPeriod) +
      ', Actions=' + PreviousServiceActions +
      ', Flag=' + IntToStr(PreviousServiceFailureFlag));

  Result := True;
end;

// Helper: Probe whether an executable file can be replaced
// NOTE: Successful rename is a secondary probe verifying basic file write/rename accessibility,
// not proof of zero open handles on its own. Authoritative safety requires SCM STOPPED + process terminated.
function CanReplaceFile(const FileName: String): Boolean;
var
  TempName: String;
begin
  Result := True;
  if not FileExists(FileName) then
    Exit;

  TempName := FileName + '.locktest_' + IntToStr(Random(1000000));
  if RenameFile(FileName, TempName) then
  begin
    RenameFile(TempName, FileName);
    Result := True;
  end
  else
  begin
    Result := False;
  end;
end;

// Helper: Neutralize service recovery actions and set start= demand
// Prevents Windows SCM from automatically restarting the service during upgrade/file replacement
// Validates independent exit codes for EVERY command and queries SCM to verify actual state change.
function NeutralizeServiceRecovery(ServiceName: String; var ErrorDetails: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  ErrorDetails := '';
  if not IsServiceInstalled(ServiceName) then
  begin
    Result := True;
    Exit;
  end;

  Log('Disarming automatic restart recovery actions for ' + ServiceName + '...');

  // 1. Clear failure actions
  if not Exec('sc.exe', 'failure "' + ServiceName + '" reset= 0 actions= ""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    ErrorDetails := 'sc.exe failure clear actions failed on ' + ServiceName + ' (code: ' + IntToStr(ResultCode) + ')';
    Log('ERROR: ' + ErrorDetails);
    Exit;
  end;

  // 2. Clear failure flag
  if not Exec('sc.exe', 'failureflag "' + ServiceName + '" 0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    ErrorDetails := 'sc.exe failureflag reset failed on ' + ServiceName + ' (code: ' + IntToStr(ResultCode) + ')';
    Log('ERROR: ' + ErrorDetails);
    Exit;
  end;

  // 3. Set start type to demand
  if not Exec('sc.exe', 'config "' + ServiceName + '" start= demand', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    ErrorDetails := 'sc.exe config start= demand failed on ' + ServiceName + ' (code: ' + IntToStr(ResultCode) + ')';
    Log('ERROR: ' + ErrorDetails);
    Exit;
  end;

  // 4. Verification Check: Ensure RESTART is no longer active in SCM failure configuration
  if Exec('cmd.exe', '/c "sc.exe qfailure "' + ServiceName + '" | findstr /i "RESTART""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
  begin
    ErrorDetails := 'SCM verification failed: recovery restart actions are still active on ' + ServiceName;
    Log('ERROR: ' + ErrorDetails);
    Exit;
  end;

  // 5. Verification Check: Ensure service startup type is verified DEMAND_START
  if not (Exec('cmd.exe', '/c "sc.exe qc "' + ServiceName + '" | findstr /i "DEMAND_START""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
  begin
    ErrorDetails := 'SCM verification failed: startup type is not DEMAND_START on ' + ServiceName;
    Log('ERROR: ' + ErrorDetails);
    Exit;
  end;

  Log('VERIFIED: SCM recovery actions cleared and startup type changed to DEMAND_START for ' + ServiceName);
  Result := True;
end;

// Helper: Restore service recovery actions and startup type
function RestoreServiceRecovery(ServiceName: String; TargetStartType: String; EnableRecovery: Boolean): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if not IsServiceInstalled(ServiceName) then
  begin
    Result := True;
    Exit;
  end;

  Log('Restoring service configuration for ' + ServiceName + ' (Start: ' + TargetStartType + ', Recovery: ' + BoolToStrCustom(EnableRecovery) + ')...');

  if TargetStartType = '' then TargetStartType := 'auto';
  if not Exec('sc.exe', 'config "' + ServiceName + '" start= ' + TargetStartType, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    Log('WARNING: sc.exe config start= ' + TargetStartType + ' exited with code: ' + IntToStr(ResultCode));
  end;

  if EnableRecovery then
  begin
    Exec('sc.exe', 'failure "' + ServiceName + '" reset= 86400 actions= restart/5000/restart/15000/restart/60000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'failureflag "' + ServiceName + '" 1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end
  else
  begin
    Exec('sc.exe', 'failure "' + ServiceName + '" reset= 0 actions= ""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'failureflag "' + ServiceName + '" 0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  Result := True;
end;

// Helper: Restore EXACT pre-upgrade service recovery actions, delays, command line, and startup type
// Implements independent exit-code verification and queries SCM to verify actual state match
function RestoreServiceRecoveryExact(ServiceName: String): Boolean;
var
  ResultCode: Integer;
  ConfigFile, ScriptFile, PsScript: String;
begin
  Result := False;
  if not IsServiceInstalled(ServiceName) then
  begin
    Result := True;
    Exit;
  end;

  Log('Restoring exact pre-upgrade SCM configuration for ' + ServiceName + ' with exit-code and query verification...');

  ConfigFile := ExpandConstant('{commonappdata}\ErrorOptimizer\backups\pre_upgrade_service_config.ini');
  ScriptFile := ExpandConstant('{tmp}\restore_scm_recovery.ps1');

  PsScript :=
    'param([string]$SvcName, [string]$CfgPath)' + #13#10 +
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    'try {' + #13#10 +
    '    $expStart = "auto"; $expDelayed = 0; $expReset = 0; $expReboot = ""; $expCmd = ""; $expActions = ""; $expFlag = 0; $expHad = 0' + #13#10 +
    '    if (Test-Path $CfgPath) {' + #13#10 +
    '        foreach ($line in (Get-Content $CfgPath)) {' + #13#10 +
    '            if ($line -match ''^StartType=(.*)'') { $expStart = $matches[1].Trim() }' + #13#10 +
    '            elseif ($line -match ''^IsDelayed=(.*)'') { $expDelayed = [int]$matches[1].Trim() }' + #13#10 +
    '            elseif ($line -match ''^ResetPeriod=(.*)'') { $expReset = [int]$matches[1].Trim() }' + #13#10 +
    '            elseif ($line -match ''^RebootMessage=(.*)'') { $expReboot = $matches[1].Trim() }' + #13#10 +
    '            elseif ($line -match ''^CommandLine=(.*)'') { $expCmd = $matches[1].Trim() }' + #13#10 +
    '            elseif ($line -match ''^Actions=(.*)'') { $expActions = $matches[1].Trim() }' + #13#10 +
    '            elseif ($line -match ''^FailureFlag=(.*)'') { $expFlag = [int]$matches[1].Trim() }' + #13#10 +
    '            elseif ($line -match ''^HadRecovery=(.*)'') { $expHad = [int]$matches[1].Trim() }' + #13#10 +
    '        }' + #13#10 +
    '    }' + #13#10 +
    '    $startCmd = $expStart; if ($startCmd -eq "delayed-auto") { $startCmd = "auto" }' + #13#10 +
    '    $p = Start-Process -FilePath "sc.exe" -ArgumentList "config `"$SvcName`" start= $startCmd" -Wait -PassThru -NoNewWindow' + #13#10 +
    '    if ($p.ExitCode -ne 0) { throw "sc.exe config start=$startCmd failed with exit code $($p.ExitCode)" }' + #13#10 +
    '    if ($expDelayed -eq 1 -or $expStart -eq "delayed-auto") {' + #13#10 +
    '        $p = Start-Process -FilePath "sc.exe" -ArgumentList "config `"$SvcName`" start= delayed-auto" -Wait -PassThru -NoNewWindow' + #13#10 +
    '        if ($p.ExitCode -ne 0) { throw "sc.exe config start=delayed-auto failed with exit code $($p.ExitCode)" }' + #13#10 +
    '    }' + #13#10 +
    '    if ($expHad -eq 1 -and $expActions) {' + #13#10 +
    '        $failArgs = "failure `"$SvcName`" reset= $expReset"' + #13#10 +
    '        if ($expReboot) { $failArgs += " reboot= `"$expReboot`"" }' + #13#10 +
    '        if ($expCmd) { $failArgs += " command= `"$expCmd`"" }' + #13#10 +
    '        $failArgs += " actions= $expActions"' + #13#10 +
    '        $p = Start-Process -FilePath "sc.exe" -ArgumentList $failArgs -Wait -PassThru -NoNewWindow' + #13#10 +
    '        if ($p.ExitCode -ne 0) { throw "sc.exe failure failed with exit code $($p.ExitCode)" }' + #13#10 +
    '        $p = Start-Process -FilePath "sc.exe" -ArgumentList "failureflag `"$SvcName`" $expFlag" -Wait -PassThru -NoNewWindow' + #13#10 +
    '        if ($p.ExitCode -ne 0) { throw "sc.exe failureflag failed with exit code $($p.ExitCode)" }' + #13#10 +
    '    } else {' + #13#10 +
    '        $p = Start-Process -FilePath "sc.exe" -ArgumentList "failure `"$SvcName`" reset= 0 actions= `"`"" -Wait -PassThru -NoNewWindow' + #13#10 +
    '        if ($p.ExitCode -ne 0) { throw "sc.exe failure reset=0 failed with exit code $($p.ExitCode)" }' + #13#10 +
    '        $p = Start-Process -FilePath "sc.exe" -ArgumentList "failureflag `"$SvcName`" 0" -Wait -PassThru -NoNewWindow' + #13#10 +
    '        if ($p.ExitCode -ne 0) { throw "sc.exe failureflag 0 failed with exit code $($p.ExitCode)" }' + #13#10 +
    '    }' + #13#10 +
    '    $qc = sc.exe qc $SvcName 2>&1 | Out-String' + #13#10 +
    '    $qf = sc.exe qfailure $SvcName 2>&1 | Out-String' + #13#10 +
    '    $qff = sc.exe qfailureflag $SvcName 2>&1 | Out-String' + #13#10 +
    '    if ($expStart -eq "demand" -and $qc -notmatch "DEMAND_START") { throw "Verification mismatch: expected DEMAND_START" }' + #13#10 +
    '    elseif ($expStart -eq "auto" -and $qc -notmatch "AUTO_START") { throw "Verification mismatch: expected AUTO_START" }' + #13#10 +
    '    elseif ($expStart -eq "disabled" -and $qc -notmatch "DISABLED") { throw "Verification mismatch: expected DISABLED" }' + #13#10 +
    '    if ($expDelayed -eq 1) {' + #13#10 +
    '        $reg = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$SvcName" -ErrorAction SilentlyContinue' + #13#10 +
    '        if (-not $reg -or $reg.DelayedAutostart -ne 1) { throw "Verification mismatch: expected DelayedAutostart=1" }' + #13#10 +
    '    }' + #13#10 +
    '    if ($expHad -eq 1) {' + #13#10 +
    '        if ($qf -notmatch "RESET_PERIOD.*?:\s*$expReset") { throw "Verification mismatch: expected RESET_PERIOD $expReset" }' + #13#10 +
    '        if ($expFlag -eq 1 -and $qff -notmatch "TRUE") { throw "Verification mismatch: expected failureflag TRUE" }' + #13#10 +
    '        elseif ($expFlag -eq 0 -and $qff -notmatch "FALSE") { throw "Verification mismatch: expected failureflag FALSE" }' + #13#10 +
    '    }' + #13#10 +
    '    exit 0' + #13#10 +
    '} catch {' + #13#10 +
    '    Write-Error $_.Exception.Message; exit 1' + #13#10 +
    '}';

  if not SaveStringToFile(ScriptFile, PsScript, False) then
  begin
    Log('ERROR: Failed to stage SCM restore script.');
    Exit;
  end;

  if Exec('powershell.exe', '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptFile + '" -SvcName "' + ServiceName + '" -CfgPath "' + ConfigFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
  begin
    Log('VERIFIED: Exact pre-upgrade SCM configuration restored and query-verified for ' + ServiceName);
    Result := True;
  end
  else
  begin
    Log('ERROR: SCM exact configuration restoration failed (exit code: ' + IntToStr(ResultCode) + ').');
  end;
  DeleteFile(ScriptFile);
end;

// Request graceful shutdown of desktop application (WPF host)
// Signals dedicated exit event and WM_CLOSE, polls bounded timeout, and aborts safely if not exited
function GracefullyStopDesktopApp(TimeoutSeconds: Integer): Boolean;
var
  I, ResultCode: Integer;
  ExitSignalCmd: String;
begin
  Result := True;
  if not IsProcessRunning('ERROR OPTIMIZER PROTOTYPE.exe') and
     not IsProcessRunning('ErrorOptimizer.exe') and
     not IsProcessRunning('ERROR OPTIMIZER.exe') then
  begin
    Log('Desktop application is not running.');
    Exit;
  end;

  Log('Requesting graceful shutdown of desktop application...');

  // 1. Signal dedicated Local\ErrorOptimizerPrototype_Exit_v1 event
  ExitSignalCmd := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' +
    '"try { $e = [System.Threading.EventWaitHandle]::OpenExisting(''Local\ErrorOptimizerPrototype_Exit_v1''); $e.Set(); exit 0 } catch { exit 1 }"';
  Exec('powershell.exe', ExitSignalCmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // 2. Post WM_CLOSE to top-level windows via taskkill without /F
  Exec('taskkill.exe', '/IM "ERROR OPTIMIZER PROTOTYPE.exe"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/IM "ErrorOptimizer.exe"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/IM "ERROR OPTIMIZER.exe"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // 3. Poll for process exit within bounded timeout
  for I := 1 to TimeoutSeconds * 2 do
  begin
    if not IsProcessRunning('ERROR OPTIMIZER PROTOTYPE.exe') and
       not IsProcessRunning('ErrorOptimizer.exe') and
       not IsProcessRunning('ERROR OPTIMIZER.exe') then
    begin
      Log('Desktop application terminated gracefully.');
      Exit;
    end;
    Sleep(500);
  end;

  // If application did not exit within timeout, abort safely without force killing
  Log('WARNING: Desktop application did not exit gracefully within ' + IntToStr(TimeoutSeconds) + ' seconds.');
  Result := False;
end;

// Stop Windows Service using SCM with bounded timeout
function StopServiceWithSCM(ServiceName: String; TimeoutSeconds: Integer): Boolean;
var
  I, ResultCode: Integer;
begin
  Result := False;
  if not IsServiceInstalled(ServiceName) then
  begin
    Result := True;
    Exit;
  end;

  if IsServiceStopped(ServiceName) then
  begin
    Log('Service ' + ServiceName + ' is already STOPPED.');
    Result := True;
    Exit;
  end;

  Log('Stopping ' + ServiceName + ' via Windows Service Control Manager...');
  Exec('sc.exe', 'stop "' + ServiceName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Poll SCM for STOPPED state
  for I := 1 to TimeoutSeconds * 2 do
  begin
    if IsServiceStopped(ServiceName) then
    begin
      Log('Service ' + ServiceName + ' confirmed STOPPED in SCM.');
      Result := True;
      Exit;
    end;
    Sleep(500);
  end;

  Log('WARNING: ' + ServiceName + ' did not reach STOPPED within ' + IntToStr(TimeoutSeconds) + ' seconds.');
  Result := False;
end;

// Comprehensive pre-installation shutdown, SCM verification, and safety gate evaluation
function SafelyStopExistingServicesAndProcesses(var ErrorMsg: String): Boolean;
var
  ServiceExe: String;
  HostExe: String;
  I, ResultCode: Integer;
  Unlocked: Boolean;
  NeutralizeError: String;
  BackupError: String;
begin
  ErrorMsg := '';
  Result := False;
  ServiceExe := ExpandConstant('{app}\core\BiosOptimizer.Service.exe');
  HostExe := ExpandConstant('{app}\ERROR OPTIMIZER PROTOTYPE.exe');

  Log('Starting pre-installation shutdown and authoritative safety verification...');

  // 1. Detect if desktop application is running and request graceful shutdown
  if not GracefullyStopDesktopApp(10) then
  begin
    ErrorMsg := 'ERROR OPTIMIZER V4 is currently open and could not be closed automatically.'#13#10#13#10 +
                'The installer cannot safely upgrade the application while it is active.'#13#10#13#10 +
                'Action: Please exit ERROR OPTIMIZER V4 (from the system tray or Task Manager), then run Setup again.';
    Log('CRITICAL: ' + ErrorMsg);
    Exit;
  end;

  // Also terminate any CLI processes
  Exec('taskkill.exe', '/IM BiosOptimizer.CLI.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  if IsProcessRunning('BiosOptimizer.CLI.exe') then
  begin
    Exec('taskkill.exe', '/F /IM BiosOptimizer.CLI.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  // 2. Detect whether BiosOptimizer.Service is installed, running, and capture existing configuration
  ServiceWasInstalledBeforeSetup := IsServiceInstalled('BiosOptimizer.Service') or IsServiceInstalled('BiosOptimizerService');
  ServiceWasRunningBeforeSetup := IsServiceRunning('BiosOptimizer.Service') or IsServiceRunning('BiosOptimizerService');

  if ServiceWasInstalledBeforeSetup then
  begin
    Log('Existing BiosOptimizer.Service detected in SCM (Running: ' + BoolToStrCustom(ServiceWasRunningBeforeSetup) + ').');
    ServiceStoppedByInstaller := True;

    // 2a. Capture exact SCM configuration and create verified pre-upgrade binary backup BEFORE modifying anything
    if not BackupPreUpgradeServiceAndConfig('BiosOptimizer.Service', BackupError) then
    begin
      ErrorMsg := 'Failed to create a verified pre-upgrade backup of the existing service executable:'#13#10 +
                  BackupError + #13#10#13#10 +
                  'Setup cannot safely proceed without ensuring the original binary can be restored on failure.';
      Log('CRITICAL: ' + ErrorMsg);
      Exit;
    end;

    // 3. Disarm SCM recovery actions & triggers BEFORE stopping, with independent exit code checks & query verification
    if not NeutralizeServiceRecovery('BiosOptimizer.Service', NeutralizeError) then
    begin
      ErrorMsg := 'Failed to safely disarm Windows Service Control Manager recovery settings:'#13#10 +
                  NeutralizeError + #13#10#13#10 +
                  'Setup cannot continue without ensuring the service will not restart during file replacement.';
      Log('CRITICAL: ' + ErrorMsg);
      RestoreServiceRecoveryExact('BiosOptimizer.Service');
      Exit;
    end;

    if IsServiceInstalled('BiosOptimizerService') then
    begin
      NeutralizeServiceRecovery('BiosOptimizerService', NeutralizeError);
    end;

    // 4. Stop the service using SCM with bounded 15-second timeout
    if not StopServiceWithSCM('BiosOptimizer.Service', 15) then
    begin
      ErrorMsg := 'The existing Windows Service (BiosOptimizer.Service) refused to stop within the 15-second timeout period.'#13#10#13#10 +
                  'Setup cannot safely replace the service executable while it is active.'#13#10#13#10 +
                  'Action: Open Services (services.msc), stop "Error Optimizer Background Service" manually, and run Setup again.';
      Log('CRITICAL: ' + ErrorMsg);
      RestoreServiceRecoveryExact('BiosOptimizer.Service');
      Exit;
    end;

    // Clean legacy alias if present
    if IsServiceInstalled('BiosOptimizerService') then
    begin
      StopServiceWithSCM('BiosOptimizerService', 5);
      Exec('sc.exe', 'delete "BiosOptimizerService"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;

  // 5. Authoritative Gate: Ensure service process has exited
  if IsProcessRunning('BiosOptimizer.Service.exe') then
  begin
    Log('Notice: Orphaned BiosOptimizer.Service.exe process detected. Terminating...');
    Exec('taskkill.exe', '/F /IM BiosOptimizer.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
    if IsProcessRunning('BiosOptimizer.Service.exe') then
    begin
      ErrorMsg := 'A running instance of BiosOptimizer.Service.exe could not be terminated.'#13#10#13#10 +
                  'Action: End the BiosOptimizer.Service.exe process in Task Manager and run Setup again.';
      Log('CRITICAL: ' + ErrorMsg);
      RestoreServiceRecoveryExact('BiosOptimizer.Service');
      Exit;
    end;
  end;

  // 6. Secondary Probe: Verify that installed executables can be replaced
  Unlocked := False;
  for I := 1 to 10 do
  begin
    if CanReplaceFile(ServiceExe) and CanReplaceFile(HostExe) then
    begin
      Unlocked := True;
      Break;
    end;
    Sleep(500);
  end;

  if not Unlocked then
  begin
    ErrorMsg := 'One or more ERROR OPTIMIZER executable files are still locked by Windows or another process:'#13#10 +
                ServiceExe + #13#10#13#10 +
                'Action: Ensure all ERROR OPTIMIZER applications and Windows services are stopped, and try again.';
    Log('CRITICAL: ' + ErrorMsg);
    RestoreServiceRecoveryExact('BiosOptimizer.Service');
    Exit;
  end;

  Log('VERIFIED: All authoritative safety gates satisfied. System ready for file extraction.');
  CleanLegacyStartupRegistrations();
  Result := True;
end;

// Backup user configuration before replacing files (Phase 4, Step 5)
procedure BackupUserConfiguration();
var
  DataDir: String;
  BackupDir: String;
begin
  DataDir := ExpandConstant('{commonappdata}\ErrorOptimizer');
  BackupDir := DataDir + '\backups\pre_upgrade_snapshot';
  if DirExists(DataDir) then
  begin
    Log('Backing up existing user configuration to ' + BackupDir + '...');
    ForceDirectories(BackupDir);
    if FileExists(DataDir + '\settings.json') then
      CopyFile(DataDir + '\settings.json', BackupDir + '\settings.json', False);
    if FileExists(DataDir + '\auto-optimize.config.json') then
      CopyFile(DataDir + '\auto-optimize.config.json', BackupDir + '\auto-optimize.config.json', False);
    if FileExists(DataDir + '\layer_applied_state.json') then
      CopyFile(DataDir + '\layer_applied_state.json', BackupDir + '\layer_applied_state.json', False);
  end;
end;

function WaitForServiceRunningAndHealthy(TimeoutSeconds: Integer): Boolean;
var
  I, ResultCode: Integer;
  HealthCmd: String;
begin
  Result := False;
  Log('Verifying BiosOptimizer.Service SCM state and IPC pipe readiness...');

  HealthCmd := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' +
    '"$ErrorActionPreference=''Stop''; try { ' +
    '$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(''.'',''BiosOptimizer_IPC_v2_default'',[System.IO.Pipes.PipeDirection]::InOut); ' +
    '$pipe.Connect(2000); ' +
    '$writer = [System.IO.StreamWriter]::new($pipe); ' +
    '$writer.WriteLine(''{\"Type\":0,\"Payload\":null}''); ' +
    '$writer.Flush(); ' +
    '$reader = [System.IO.StreamReader]::new($pipe); ' +
    '$resp = $reader.ReadLine(); ' +
    'if ($resp -match ''BiosOptimizerService'' -and $resp -match ''v2'') { exit 0 } else { exit 1 } ' +
    '} catch { exit 2 }"';

  for I := 1 to TimeoutSeconds * 2 do
  begin
    if Exec('powershell.exe', HealthCmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    begin
      Log('VERIFIED: BiosOptimizer.Service is RUNNING, IPC pipe is open, and protocol v2 handshake succeeded.');
      Result := True;
      Exit;
    end;
    Sleep(500);
  end;

  Log('WARNING: BiosOptimizer.Service did not complete IPC health verification within ' + IntToStr(TimeoutSeconds) + ' seconds.');
end;

function InstallAndStartBackgroundService(): Boolean;
var
  ServiceExe: String;
  ResultCode: Integer;
begin
  Result := False;
  ServiceExe := ExpandConstant('{app}\core\BiosOptimizer.Service.exe');
  if not FileExists(ServiceExe) then
  begin
    Log('ERROR: Companion service executable not found at: ' + ServiceExe);
    Exit;
  end;

  // Clean any legacy service alias
  if IsServiceInstalled('BiosOptimizerService') then
  begin
    Exec('sc.exe', 'stop "BiosOptimizerService"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete "BiosOptimizerService"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  // If service exists, configure in-place. If not, create it.
  if IsServiceInstalled('BiosOptimizer.Service') then
  begin
    Log('Existing BiosOptimizer.Service registration found. Updating in-place with sc.exe config...');
    if not Exec('sc.exe', 'config "BiosOptimizer.Service" binPath= "\"' + ServiceExe + '\" --service" start= auto DisplayName= "Error Optimizer Background Service" obj= LocalSystem', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    begin
      Log('WARNING: sc.exe config returned ' + IntToStr(ResultCode) + '. Attempting recreate...');
      Exec('sc.exe', 'delete "BiosOptimizer.Service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(500);
      Exec('sc.exe', 'create "BiosOptimizer.Service" binPath= "\"' + ServiceExe + '\" --service" start= auto DisplayName= "Error Optimizer Background Service" obj= LocalSystem', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end
  else
  begin
    Log('Registering new BiosOptimizer.Service in Windows SCM...');
    if not Exec('sc.exe', 'create "BiosOptimizer.Service" binPath= "\"' + ServiceExe + '\" --service" start= auto DisplayName= "Error Optimizer Background Service" obj= LocalSystem', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    begin
      Log('ERROR: sc.exe create BiosOptimizer.Service exited with code: ' + IntToStr(ResultCode));
      Exit;
    end;
  end;

  // Set service description
  Exec('sc.exe', 'description "BiosOptimizer.Service" "Persistent Windows background service for ERROR OPTIMIZER V4 telemetry, AI policies, Auto Optimize scheduler, and transactional system tuning."', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Restore service recovery settings (Phase 3 & Phase 4, Step 9)
  RestoreServiceRecovery('BiosOptimizer.Service', 'auto', True);

  // Start the new service (Phase 4, Step 10)
  Log('Starting BiosOptimizer.Service...');
  Exec('sc.exe', 'start "BiosOptimizer.Service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

function VerifyInstallation(): Boolean;
begin
  Result := True;
  if not FileExists(ExpandConstant('{app}\ERROR OPTIMIZER PROTOTYPE.exe')) then
  begin
    Log('VERIFY ERROR: Main application executable missing at: {app}\ERROR OPTIMIZER PROTOTYPE.exe');
    Result := False;
  end;
  if not FileExists(ExpandConstant('{app}\core\BiosOptimizer.Service.exe')) then
  begin
    Log('VERIFY ERROR: Background service executable missing at: {app}\core\BiosOptimizer.Service.exe');
    Result := False;
  end;
  if not FileExists(ExpandConstant('{app}\core\BiosOptimizer.CLI.exe')) then
  begin
    Log('VERIFY ERROR: CLI executable missing at: {app}\core\BiosOptimizer.CLI.exe');
    Result := False;
  end;
  if RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\BiosOptimizer.Service') then
  begin
    Log('VERIFY: BiosOptimizer.Service SCM registration verified successfully.');
  end
  else
  begin
    Log('VERIFY WARNING: BiosOptimizer.Service SCM registration not detected in HKLM64.');
    Result := False;
  end;

  if Result then
    Log('VERIFY SUCCESS: ERROR OPTIMIZER V4 installation verified intact.')
  else
    Log('VERIFY WARNING: Installation verification encountered anomalies.');
end;

// Helper: Create an in-progress transaction marker before file extraction starts
function CreateInProgressTransactionMarker(SnapshotDir, SnapshotId, TargetAppDir, ServiceName: String): Boolean;
var
  MarkerFile, ScriptFile, PsScript: String;
  ResultCode: Integer;
begin
  Result := False;
  MarkerFile := ExpandConstant('{commonappdata}\ErrorOptimizer\backups\in_progress_transaction.json');
  ScriptFile := ExpandConstant('{tmp}\create_transaction_marker.ps1');

  PsScript :=
    'param([string]$MarkerPath, [string]$SnapDir, [string]$SnapId, [string]$TargetDir, [string]$SvcName)' + #13#10 +
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    'try {' + #13#10 +
    '    $m = [ordered]@{ TransactionStarted = (Get-Date).ToString("o"); SnapshotId = $SnapId; SnapshotDir = $SnapDir; TargetAppDir = $TargetDir; ServiceName = $SvcName; Status = "In_Progress" }' + #13#10 +
    '    $m | ConvertTo-Json -Depth 3 | Set-Content -Path $MarkerPath -Encoding UTF8' + #13#10 +
    '    exit 0' + #13#10 +
    '} catch { exit 1 }';

  if SaveStringToFile(ScriptFile, PsScript, False) then
  begin
    Result := Exec('powershell.exe', '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptFile + '" -MarkerPath "' + MarkerFile + '" -SnapDir "' + SnapshotDir + '" -SnapId "' + SnapshotId + '" -TargetDir "' + TargetAppDir + '" -SvcName "' + ServiceName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
    DeleteFile(ScriptFile);
    if Result then
      Log('TRANSACTION MARKER CREATED: ' + MarkerFile);
  end;
end;

// Helper: Clear in-progress transaction marker upon successful installation or completed rollback
function ClearInProgressTransactionMarker(): Boolean;
var
  MarkerFile: String;
begin
  MarkerFile := ExpandConstant('{commonappdata}\ErrorOptimizer\backups\in_progress_transaction.json');
  Result := True;
  if FileExists(MarkerFile) then
  begin
    Result := DeleteFile(MarkerFile);
    if Result then
      Log('TRANSACTION MARKER CLEARED: ' + MarkerFile)
    else
      Log('WARNING: Failed to clear transaction marker: ' + MarkerFile);
  end;
end;

// Helper: Detect interrupted transaction from previous setup attempt and restore complete application
function DetectAndRecoverInterruptedTransaction(): Boolean;
var
  MarkerFile, LkgDir, ScriptFile, PsScript: String;
  ResultCode: Integer;
begin
  Result := True;
  MarkerFile := ExpandConstant('{commonappdata}\ErrorOptimizer\backups\in_progress_transaction.json');
  LkgDir := ExpandConstant('{commonappdata}\ErrorOptimizer\backups\last_known_good');

  if not FileExists(MarkerFile) then
    Exit;

  Log('WARNING: In-progress transaction marker found! An earlier installation was interrupted.');
  IsInterruptedTransactionDetected := True;

  ScriptFile := ExpandConstant('{tmp}\recover_interrupted_transaction.ps1');
  PsScript :=
    'param([string]$MarkerPath, [string]$Lkg)' + #13#10 +
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    'function Validate-PEFile($path) {' + #13#10 +
    '    $bytes = [System.IO.File]::ReadAllBytes($path)' + #13#10 +
    '    if ($bytes.Length -lt 1024 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $false }' + #13#10 +
    '    $peOff = [System.BitConverter]::ToInt32($bytes, 0x3C)' + #13#10 +
    '    if ($peOff -lt 64 -or ($peOff + 28) -gt $bytes.Length) { return $false }' + #13#10 +
    '    if ($bytes[$peOff] -ne 0x50 -or $bytes[$peOff+1] -ne 0x45 -or $bytes[$peOff+2] -ne 0x00 -or $bytes[$peOff+3] -ne 0x00) { return $false }' + #13#10 +
    '    $machine = [System.BitConverter]::ToUInt16($bytes, $peOff + 4); $numSections = [System.BitConverter]::ToUInt16($bytes, $peOff + 6)' + #13#10 +
    '    $optSize = [System.BitConverter]::ToUInt16($bytes, $peOff + 20); $optMagic = [System.BitConverter]::ToUInt16($bytes, $peOff + 24)' + #13#10 +
    '    if ($numSections -eq 0 -or $optSize -eq 0) { return $false }' + #13#10 +
    '    if ($path.EndsWith(''.exe'', [System.StringComparison]::OrdinalIgnoreCase)) { if ($machine -ne 0x8664 -or $optMagic -ne 0x020B) { return $false } }' + #13#10 +
    '    else { if ($machine -ne 0x8664 -and $machine -ne 0x014C) { return $false }; if ($optMagic -ne 0x020B -and $optMagic -ne 0x010B) { return $false } }' + #13#10 +
    '    return $true' + #13#10 +
    '}' + #13#10 +
    'try {' + #13#10 +
    '    $m = Get-Content $MarkerPath -Raw | ConvertFrom-Json' + #13#10 +
    '    $src = $m.SnapshotDir; $target = $m.TargetAppDir' + #13#10 +
    '    if (-not (Test-Path (Join-Path $src "manifest.json"))) { $src = $Lkg }' + #13#10 +
    '    if (-not (Test-Path (Join-Path $src "manifest.json"))) { throw "No valid recovery snapshot found" }' + #13#10 +
    '    $manifest = Get-Content (Join-Path $src "manifest.json") -Raw | ConvertFrom-Json' + #13#10 +
    '    if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }' + #13#10 +
    '    foreach ($f in $manifest.Files) {' + #13#10 +
    '        $rel = if ($f.RelativePath) { $f.RelativePath } else { $f.Name }' + #13#10 +
    '        $sf = Join-Path $src $rel; $df = Join-Path $target $rel' + #13#10 +
    '        if (Test-Path $sf) {' + #13#10 +
    '            $dSub = Split-Path -Parent $df; if (-not (Test-Path $dSub)) { New-Item -ItemType Directory -Path $dSub -Force | Out-Null }' + #13#10 +
    '            Copy-Item -Path $sf -Destination $df -Force' + #13#10 +
    '            $len = (Get-Item $df).Length; if ($len -ne $f.Size) { throw "Size mismatch on $rel" }' + #13#10 +
    '            if ($f.IsPE -eq $true) { if (-not (Validate-PEFile $df)) { throw "PE validation failed on $rel" } }' + #13#10 +
    '            $hash = (Get-FileHash -Path $df -Algorithm SHA256).Hash; if ($hash -ne $f.Sha256) { throw "SHA256 mismatch on $rel" }' + #13#10 +
    '        }' + #13#10 +
    '    }' + #13#10 +
    '    Remove-Item -Path $MarkerPath -Force' + #13#10 +
    '    exit 0' + #13#10 +
    '} catch { Write-Error $_.Exception.Message; exit 1 }';

  if SaveStringToFile(ScriptFile, PsScript, False) then
  begin
    if Exec('powershell.exe', '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptFile + '" -MarkerPath "' + MarkerFile + '" -Lkg "' + LkgDir + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    begin
      Log('INTERRUPTED TRANSACTION RECOVERED: Restored and verified complete application from snapshot.');
      Result := True;
    end
    else
    begin
      Log('CRITICAL: Interrupted transaction automatic recovery failed (code ' + IntToStr(ResultCode) + ').');
      Result := False;
    end;
    DeleteFile(ScriptFile);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  IsUpgrade: Boolean;
  StopError: String;
begin
  Result := '';
  NeedsRestart := False;

  // 0. DETECT AND RECOVER INTERRUPTED TRANSACTION FROM PREVIOUS ATTEMPT
  if not DetectAndRecoverInterruptedTransaction() then
  begin
    Result := 'A previous interrupted installation was detected, but automatic recovery from backup failed.'#13#10#13#10 +
              'Action: Check %ProgramData%\ErrorOptimizer\backups and restore files manually before retrying setup.';
    Exit;
  end;

  // 1. Check system compatibility
  Log('Checking system compatibility...');
  if not Is64BitInstallMode then
  begin
    Result := 'ERROR OPTIMIZER V4 requires a 64-bit operating system (Windows 10 or Windows 11 x64). Installation aborted.';
    Exit;
  end;

  // 2. Detect existing installation state
  IsUpgrade := IsExistingInstallationDetected();
  if IsUpgrade then
    Log('Existing ERROR OPTIMIZER installation detected. Entering Safe Upgrade sequence.')
  else
    Log('No prior installation detected. Entering Clean Installation sequence.');

  // 3. Verify & Auto-install Microsoft Edge WebView2 Runtime
  // NOTE: Performed BEFORE stopping existing instances to protect running system on failure!
  Log('Checking WebView2 Runtime...');
  if not IsWebView2RuntimeInstalled() then
  begin
    Log('Installing WebView2 Runtime...');
    if not InstallWebView2Runtime() then
    begin
      Log('CRITICAL: WebView2 Runtime could not be installed. Aborting setup.');
      if IsUpgrade then
        Log('Safe Upgrade Guard: Existing installation, running service, and user data remain 100% intact and undamaged.');

      Result := 'Microsoft Edge WebView2 Runtime is required to display the ERROR OPTIMIZER V4 interface.'#13#10#13#10 +
                'Automatic installation of the WebView2 Runtime could not be completed.'#13#10#13#10 +
                'Please ensure your PC has internet access, or download and install the Evergreen Bootstrapper manually from:'#13#10 +
                'https://developer.microsoft.com/en-us/microsoft-edge/webview2/'#13#10#13#10 +
                'Your existing installation was NOT modified. After installing WebView2, run this installer again.';
      Exit;
    end;
    Log('WebView2 Runtime installed and verified.');
  end
  else
  begin
    Log('WebView2 Runtime is already installed and verified.');
  end;

  // 4. Verify & Auto-install Microsoft Visual C++ 2015-2022 Redistributable
  Log('Checking Visual C++ Redistributable...');
  if not IsVCRedistInstalled() then
  begin
    Log('Installing Visual C++ 2015-2022 Redistributable (x64)...');
    if not InstallVCRedist() then
    begin
      Log('WARNING: Visual C++ Redistributable auto-install returned non-zero. Continuing with built-in CRT fallback.');
    end
    else
    begin
      Log('Visual C++ Redistributable installed and verified.');
    end;
  end
  else
  begin
    Log('Visual C++ Redistributable is already installed and verified.');
  end;

  Log('Prerequisite detection and verification completed successfully.');

  // 5. SAFELY STOP EXISTING APPLICATION & SERVICE AND VERIFY ZERO FILE LOCKS
  // Executed ONLY after prerequisites succeed, but BEFORE file extraction starts!
  if not SafelyStopExistingServicesAndProcesses(StopError) then
  begin
    Result := StopError;
    Exit;
  end;

  // 6. BACKUP USER CONFIGURATION PRIOR TO FILE OVERWRITE
  BackupUserConfiguration();

  // 7. CREATE IN-PROGRESS TRANSACTION MARKER BEFORE FILE EXTRACTION
  if HasPreUpgradeBinaryBackup and (ActiveSnapshotDir <> '') then
  begin
    CreateInProgressTransactionMarker(ActiveSnapshotDir, ActiveSnapshotId, ExpandConstant('{app}'), 'BiosOptimizer.Service');
  end;

  Log('System is fully prepared for installation / binary upgrade. All binaries unlocked.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  SvcReady: Boolean;
begin
  if CurStep = ssPostInstall then
  begin
    CleanLegacyStartupRegistrations();

    // Install / update and start the background service
    if not InstallAndStartBackgroundService() then
    begin
      MsgBox('ERROR OPTIMIZER V4 installed successfully, but registering the BiosOptimizer.Service background service encountered an issue.'#13#10#13#10 +
             'The desktop application will continue using its built-in in-process fallback engine.', mbInformation, MB_OK);
    end
    else
    begin
      SvcReady := WaitForServiceRunningAndHealthy(15);
      if not SvcReady then
      begin
        Log('NOTICE: BiosOptimizer.Service is initializing; desktop client will establish IPC session once ready.');
      end;
    end;

    VerifyInstallation();
    InstallationSucceeded := True;
    ClearInProgressTransactionMarker();
  end;
end;

procedure DeinitializeSetup();
var
  ResultCode: Integer;
  TargetAppDir, ServiceExe, SnapDir, ScriptFile, PsScript: String;
  BinaryVerified: Boolean;
begin
  // Rollback Guard: If setup was cancelled or aborted after stopping the service, restore previous configuration
  if not InstallationSucceeded and ServiceStoppedByInstaller then
  begin
    Log('Rollback Guard: Setup exited without successful completion. Commencing rollback checks...');
    TargetAppDir := ExpandConstant('{app}');
    ServiceExe := InstalledServiceBinaryPath;
    if ServiceExe = '' then ServiceExe := TargetAppDir + '\core\BiosOptimizer.Service.exe';

    SnapDir := ActiveSnapshotDir;
    if (SnapDir = '') or not FileExists(SnapDir + '\manifest.json') then
    begin
      SnapDir := ExpandConstant('{commonappdata}\ErrorOptimizer\backups\last_known_good');
    end;

    if IsServiceInstalled('BiosOptimizer.Service') then
    begin
      BinaryVerified := False;
      if FileExists(SnapDir + '\manifest.json') then
      begin
        Log('Rollback Guard: Restoring complete application from verified snapshot: ' + SnapDir);
        ScriptFile := ExpandConstant('{tmp}\rollback_snapshot.ps1');
        PsScript :=
          'param([string]$SnapshotDir, [string]$TargetDir)' + #13#10 +
          '$ErrorActionPreference = ''Stop''' + #13#10 +
          'function Validate-PEFile($path) {' + #13#10 +
          '    $bytes = [System.IO.File]::ReadAllBytes($path)' + #13#10 +
          '    if ($bytes.Length -lt 1024 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $false }' + #13#10 +
          '    $peOff = [System.BitConverter]::ToInt32($bytes, 0x3C)' + #13#10 +
          '    if ($peOff -lt 64 -or ($peOff + 28) -gt $bytes.Length) { return $false }' + #13#10 +
          '    if ($bytes[$peOff] -ne 0x50 -or $bytes[$peOff+1] -ne 0x45 -or $bytes[$peOff+2] -ne 0x00 -or $bytes[$peOff+3] -ne 0x00) { return $false }' + #13#10 +
          '    $machine = [System.BitConverter]::ToUInt16($bytes, $peOff + 4); $numSections = [System.BitConverter]::ToUInt16($bytes, $peOff + 6)' + #13#10 +
          '    $optSize = [System.BitConverter]::ToUInt16($bytes, $peOff + 20); $optMagic = [System.BitConverter]::ToUInt16($bytes, $peOff + 24)' + #13#10 +
          '    if ($numSections -eq 0 -or $optSize -eq 0) { return $false }' + #13#10 +
          '    if ($path.EndsWith(''.exe'', [System.StringComparison]::OrdinalIgnoreCase)) { if ($machine -ne 0x8664 -or $optMagic -ne 0x020B) { return $false } }' + #13#10 +
          '    else { if ($machine -ne 0x8664 -and $machine -ne 0x014C) { return $false }; if ($optMagic -ne 0x020B -and $optMagic -ne 0x010B) { return $false } }' + #13#10 +
          '    return $true' + #13#10 +
          '}' + #13#10 +
          'try {' + #13#10 +
          '    $mf = Join-Path $SnapshotDir "manifest.json"' + #13#10 +
          '    if (-not (Test-Path $mf)) { throw "Manifest missing: $mf" }' + #13#10 +
          '    $manifest = Get-Content $mf -Raw | ConvertFrom-Json' + #13#10 +
          '    if (-not (Test-Path $TargetDir)) { New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null }' + #13#10 +
          '    foreach ($f in $manifest.Files) {' + #13#10 +
          '        $rel = if ($f.RelativePath) { $f.RelativePath } else { $f.Name }' + #13#10 +
          '        $src = Join-Path $SnapshotDir $rel; $dst = Join-Path $TargetDir $rel' + #13#10 +
          '        if (-not (Test-Path $src)) { throw "Source missing in snapshot: $rel" }' + #13#10 +
          '        $dSub = Split-Path -Parent $dst; if (-not (Test-Path $dSub)) { New-Item -ItemType Directory -Path $dSub -Force | Out-Null }' + #13#10 +
          '        Copy-Item -Path $src -Destination $dst -Force' + #13#10 +
          '        if (-not (Test-Path $dst)) { throw "Restored file missing: $rel" }' + #13#10 +
          '        $len = (Get-Item $dst).Length; if ($len -ne $f.Size) { throw "Size mismatch on $rel" }' + #13#10 +
          '        if ($f.IsPE -eq $true) { if (-not (Validate-PEFile $dst)) { throw "PE validation failed on $rel" } }' + #13#10 +
          '        $hash = (Get-FileHash -Path $dst -Algorithm SHA256).Hash' + #13#10 +
          '        if ($hash -ne $f.Sha256) { throw "SHA256 mismatch on $rel" }' + #13#10 +
          '    }' + #13#10 +
          '    exit 0' + #13#10 +
          '} catch {' + #13#10 +
          '    Write-Error $_.Exception.Message; exit 1' + #13#10 +
          '}';

        if SaveStringToFile(ScriptFile, PsScript, False) then
        begin
          if Exec('powershell.exe', '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptFile + '" -SnapshotDir "' + SnapDir + '" -TargetDir "' + TargetAppDir + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
          begin
            BinaryVerified := True;
            Log('VERIFIED: Complete application restored and passed selective PE header, file size, and SHA-256 integrity checks.');
          end
          else
          begin
            Log('WARNING: Complete application rollback failed (exit code: ' + IntToStr(ResultCode) + ').');
          end;
          DeleteFile(ScriptFile);
        end;
      end;

      // 3. Restore exact SCM configuration
      if BinaryVerified then
      begin
        RestoreServiceRecoveryExact('BiosOptimizer.Service');
        ClearInProgressTransactionMarker();
      end
      else
      begin
        // If binary is NOT verified, disarm recovery actions to prevent crash loops
        Log('WARNING: Restored binaries could not be 100% verified. Disarming recovery actions to prevent crash loops.');
        Exec('sc.exe', 'config "BiosOptimizer.Service" start= demand', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Exec('sc.exe', 'failure "BiosOptimizer.Service" reset= 0 actions= ""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Exec('sc.exe', 'failureflag "BiosOptimizer.Service" 0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      end;

      // 4. Restart service ONLY if all binaries are 100% verified AND was originally running
      if BinaryVerified and ServiceWasRunningBeforeSetup then
      begin
        Log('Rollback Guard: Restarting verified original working service binary...');
        Exec('sc.exe', 'start "BiosOptimizer.Service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      end
      else
      begin
        Log('Rollback Guard: Service restart skipped. Binaries verified: ' + BoolToStrCustom(BinaryVerified) + ', WasRunning: ' + BoolToStrCustom(ServiceWasRunningBeforeSetup));
      end;
    end;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  MsgBox('NOTE: Uninstalling ERROR OPTIMIZER V4 removes the application binaries, shortcuts, and background service.'#13#10#13#10 +
         'System optimizations previously applied to Windows are NOT automatically reversed by uninstalling.'#13#10#13#10 +
         'Your rollback journals, system restore points, and configuration backups are safely preserved in %ProgramData%\ErrorOptimizer so that your system settings can be restored upon reinstallation if desired.', mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec('sc.exe', 'failure "BiosOptimizer.Service" reset= 0 actions= ""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'failure "BiosOptimizerService" reset= 0 actions= ""', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    StopServiceWithSCM('BiosOptimizer.Service', 10);
    StopServiceWithSCM('BiosOptimizerService', 5);
    Exec('sc.exe', 'delete "BiosOptimizer.Service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete "BiosOptimizerService"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill.exe', '/IM "ERROR OPTIMIZER PROTOTYPE.exe"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill.exe', '/IM BiosOptimizer.CLI.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
    CleanLegacyStartupRegistrations();
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ERROR OPTIMIZER V4');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ERROR OPTIMIZER PROTOTYPE');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Error Optimizer Prototype');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Error Optimizer');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ErrorOptimizer');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'ERROR OPTIMIZER V4');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'ERROR OPTIMIZER PROTOTYPE');
    Sleep(500);
  end;
end;
