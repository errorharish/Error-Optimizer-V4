#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace BiosOptimizer.Core.Services;

public sealed class InteractiveUserInfo
{
    public bool IsAvailable { get; set; }
    public int SessionId { get; set; } = -1;
    public string? UserSid { get; set; }
    public string? UserName { get; set; }
    public string? DomainName { get; set; }
    public string ResolutionMethod { get; set; } = string.Empty;
    public string? IneligibilityReason { get; set; }
    public bool IsProfileLoaded { get; set; }

    public static InteractiveUserInfo Unavailable(string reason, int sessionId = -1) =>
        new InteractiveUserInfo
        {
            IsAvailable = false,
            SessionId = sessionId,
            IneligibilityReason = reason,
            ResolutionMethod = "None"
        };
}

public static class InteractiveUserResolver
{
    private enum WTS_INFO_CLASS
    {
        WTSInitialProgram = 0,
        WTSApplicationName = 1,
        WTSWorkingDirectory = 2,
        WTSOEMId = 3,
        WTSSessionId = 4,
        WTSUserName = 5,
        WTSWinStationName = 6,
        WTSDomainName = 7,
        WTSConnectState = 8,
        WTSClientBuildNumber = 9,
        WTSClientName = 10,
        WTSClientDirectory = 11,
        WTSClientProductId = 12,
        WTSClientHardwareId = 13,
        WTSClientAddress = 14,
        WTSClientDisplay = 15,
        WTSClientProtocolType = 16,
        WTSIdleTime = 17,
        WTSLogonTime = 18,
        WTSIncomingBytes = 19,
        WTSOutgoingBytes = 20,
        WTSIncomingFrames = 21,
        WTSOutgoingFrames = 22,
        WTSClientInfo = 23,
        WTSSessionInfo = 24
    }

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    private const uint TOKEN_QUERY = 0x0008;

    private static readonly object _syncLock = new();
    private static InteractiveUserInfo? _cachedInfo;
    private static DateTime _cacheTimestamp = DateTime.MinValue;

    /// <summary>
    /// Resolves the unambiguous active interactive console user without guessing or scanning Registry.Users.
    /// Handles LocalSystem, Session 0, multiple users, disconnected/locked sessions, and user switching.
    /// </summary>
    public static InteractiveUserInfo ResolveActiveUser(bool forceRefresh = false)
    {
        lock (_syncLock)
        {
            if (!forceRefresh && _cachedInfo != null && (DateTime.UtcNow - _cacheTimestamp).TotalSeconds < 2)
            {
                return _cachedInfo;
            }

            var info = ResolveInternal();
            _cachedInfo = info;
            _cacheTimestamp = DateTime.UtcNow;
            return info;
        }
    }

    private static InteractiveUserInfo ResolveInternal()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return InteractiveUserInfo.Unavailable("Operating system is not Windows.");
        }

        // Case 1: Running as a normal interactive user process (e.g. desktop UI / host running in user session)
        try
        {
            var curIdentity = WindowsIdentity.GetCurrent();
            if (!curIdentity.IsSystem && curIdentity.User != null)
            {
                string sid = curIdentity.User.Value;
                if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
                {
                    int sessId = Process.GetCurrentProcess().SessionId;
                    return new InteractiveUserInfo
                    {
                        IsAvailable = true,
                        SessionId = sessId,
                        UserSid = sid,
                        UserName = curIdentity.Name,
                        ResolutionMethod = "CurrentProcessUserToken",
                        IsProfileLoaded = CheckProfileLoaded(sid)
                    };
                }
            }
        }
        catch { }

        // Case 2: Running as LocalSystem / Session 0 service - must inspect the active console session
        uint consoleSessionId = WTSGetActiveConsoleSessionId();
        if (consoleSessionId == 0xFFFFFFFF)
        {
            return InteractiveUserInfo.Unavailable("No active console session attached to the host machine.");
        }

        int activeSession = (int)consoleSessionId;

        // Verify session connection state
        var connectState = GetSessionConnectState(activeSession);
        if (connectState != WTS_CONNECTSTATE_CLASS.WTSActive && connectState != WTS_CONNECTSTATE_CLASS.WTSShadow)
        {
            return InteractiveUserInfo.Unavailable(
                $"Console session {activeSession} is not active (State: {connectState}).", activeSession);
        }

        // Method A: Query User Token via WTSQueryUserToken
        try
        {
            if (WTSQueryUserToken(consoleSessionId, out IntPtr hToken))
            {
                try
                {
                    using var identity = new WindowsIdentity(hToken);
                    if (identity.User != null && identity.User.Value.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
                    {
                        string sid = identity.User.Value;
                        return new InteractiveUserInfo
                        {
                            IsAvailable = true,
                            SessionId = activeSession,
                            UserSid = sid,
                            UserName = identity.Name,
                            ResolutionMethod = "WTSQueryUserToken",
                            IsProfileLoaded = CheckProfileLoaded(sid)
                        };
                    }
                }
                finally
                {
                    CloseHandle(hToken);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InteractiveUserResolver] WTSQueryUserToken exception: {ex.Message}");
        }

        // Method B: Authoritative shell token inspection via explorer.exe in active console session
        try
        {
            var explorerProcesses = Process.GetProcessesByName("explorer")
                .Where(p => p.SessionId == activeSession)
                .ToList();

            if (explorerProcesses.Count > 0)
            {
                foreach (var exp in explorerProcesses)
                {
                    try
                    {
                        if (OpenProcessToken(exp.Handle, TOKEN_QUERY, out IntPtr pToken))
                        {
                            try
                            {
                                using var expId = new WindowsIdentity(pToken);
                                if (expId.User != null && expId.User.Value.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
                                {
                                    string sid = expId.User.Value;
                                    return new InteractiveUserInfo
                                    {
                                        IsAvailable = true,
                                        SessionId = activeSession,
                                        UserSid = sid,
                                        UserName = expId.Name,
                                        ResolutionMethod = "ConsoleShellProcessToken",
                                        IsProfileLoaded = CheckProfileLoaded(sid)
                                    };
                                }
                            }
                            finally
                            {
                                CloseHandle(pToken);
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InteractiveUserResolver] Explorer token inspection exception: {ex.Message}");
        }

        // Method C: WTSQuerySessionInformation UserName
        string? wtsUser = GetSessionString(activeSession, WTS_INFO_CLASS.WTSUserName);
        string? wtsDomain = GetSessionString(activeSession, WTS_INFO_CLASS.WTSDomainName);

        if (!string.IsNullOrWhiteSpace(wtsUser))
        {
            try
            {
                string accountName = !string.IsNullOrWhiteSpace(wtsDomain) ? $"{wtsDomain}\\{wtsUser}" : wtsUser;
                var account = new NTAccount(accountName);
                var sid = account.Translate(typeof(SecurityIdentifier)).Value;
                if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
                {
                    return new InteractiveUserInfo
                    {
                        IsAvailable = true,
                        SessionId = activeSession,
                        UserSid = sid,
                        UserName = accountName,
                        DomainName = wtsDomain,
                        ResolutionMethod = "WTSQuerySessionInformation",
                        IsProfileLoaded = CheckProfileLoaded(sid)
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InteractiveUserResolver] NTAccount translation exception: {ex.Message}");
            }
        }

        return InteractiveUserInfo.Unavailable(
            $"Unable to resolve unambiguous interactive user for console session {activeSession}.", activeSession);
    }

    private static bool CheckProfileLoaded(string sid)
    {
        try
        {
            using var userHive = Registry.Users.OpenSubKey(sid, false);
            return userHive != null;
        }
        catch
        {
            return false;
        }
    }

    private static WTS_CONNECTSTATE_CLASS GetSessionConnectState(int sessionId)
    {
        IntPtr pBuffer = IntPtr.Zero;
        try
        {
            if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTS_INFO_CLASS.WTSConnectState, out pBuffer, out int bytes) && bytes >= 4)
            {
                return (WTS_CONNECTSTATE_CLASS)Marshal.ReadInt32(pBuffer);
            }
        }
        catch { }
        finally
        {
            if (pBuffer != IntPtr.Zero) WTSFreeMemory(pBuffer);
        }
        return WTS_CONNECTSTATE_CLASS.WTSDisconnected;
    }

    private static string? GetSessionString(int sessionId, WTS_INFO_CLASS infoClass)
    {
        IntPtr pBuffer = IntPtr.Zero;
        try
        {
            if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out pBuffer, out int bytes) && bytes > 0)
            {
                return Marshal.PtrToStringAuto(pBuffer);
            }
        }
        catch { }
        finally
        {
            if (pBuffer != IntPtr.Zero) WTSFreeMemory(pBuffer);
        }
        return null;
    }
}
