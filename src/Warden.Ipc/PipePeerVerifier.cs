using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Warden.Ipc;

/// <summary>
/// The ACL keeps unauthorized SIDs off the pipe at the OS level, but it can't tell "the right
/// SID, connecting from the session I created this pipe for" apart from "the right SID,
/// connecting from somewhere else" -- e.g. a service account shared across sessions, or an ACL
/// that's simply misconfigured. <see cref="GetClientSessionId"/> resolves the real session of
/// the process on the other end of an already-connected pipe so the caller can check that too.
/// </summary>
public static class PipePeerVerifier
{
    public static int GetClientSessionId(NamedPipeServerStream connectedServer)
    {
        if (!NativeMethods.GetNamedPipeClientProcessId(connectedServer.SafePipeHandle, out var clientProcessId))
        {
            throw new InvalidOperationException(
                $"GetNamedPipeClientProcessId failed with Win32 error {Marshal.GetLastWin32Error()}");
        }

        if (!NativeMethods.ProcessIdToSessionId(clientProcessId, out var sessionId))
        {
            throw new InvalidOperationException(
                $"ProcessIdToSessionId failed with Win32 error {Marshal.GetLastWin32Error()}");
        }

        return (int)sessionId;
    }
}
