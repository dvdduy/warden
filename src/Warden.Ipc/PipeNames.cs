namespace Warden.Ipc;

public static class PipeNames
{
    /// <summary>
    /// Every logged-on session gets its own pipe -- more than one person can be logged into the
    /// same box at once (RDP, fast user switching), and nothing should let one session's
    /// user-agent see another session's messages.
    /// </summary>
    public static string ForSession(int sessionId) => $"WardenIpc-{sessionId}";
}
