using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Warden.Agent.Interop;

internal enum WtsConnectState
{
    Active,
    Connected,
    ConnectQuery,
    Shadow,
    Disconnected,
    Idle,
    Listen,
    Reset,
    Down,
    Init,
}

[StructLayout(LayoutKind.Sequential)]
internal struct WtsSessionInfo
{
    public int SessionId;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string WinStationName;
    public WtsConnectState State;
}

internal static class Wtsapi32
{
    private static readonly nint WtsCurrentServerHandle = nint.Zero;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle phToken);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(
        nint hServer, uint reserved, uint version, out nint sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    /// <summary>
    /// Requires <c>SeTcbPrivilege</c> ("act as part of the operating system") -- which only a
    /// process running as <c>LocalSystem</c> holds. An interactively-run dev build of
    /// <c>Warden.Agent</c> will fail this with <c>ERROR_PRIVILEGE_NOT_HELD</c>; that's expected
    /// and is why this path can only be verified live once the service is actually installed.
    /// </summary>
    internal static SafeAccessTokenHandle QueryUserToken(int sessionId)
    {
        if (!WTSQueryUserToken((uint)sessionId, out var token))
        {
            throw new Win32Exception($"WTSQueryUserToken failed for session {sessionId}");
        }

        return token;
    }

    internal static IReadOnlyList<(int SessionId, WtsConnectState State)> EnumerateSessions()
    {
        if (!WTSEnumerateSessionsW(WtsCurrentServerHandle, 0, 1, out var sessionInfoPtr, out var count))
        {
            throw new Win32Exception("WTSEnumerateSessionsW failed");
        }

        try
        {
            var results = new List<(int, WtsConnectState)>(count);
            var entrySize = Marshal.SizeOf<WtsSessionInfo>();

            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WtsSessionInfo>(sessionInfoPtr + i * entrySize);
                results.Add((entry.SessionId, entry.State));
            }

            return results;
        }
        finally
        {
            WTSFreeMemory(sessionInfoPtr);
        }
    }
}
