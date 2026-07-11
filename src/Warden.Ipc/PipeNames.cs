namespace Warden.Ipc;

public static class PipeNames
{
    /// <summary>
    /// Session 1's single well-known pipe name. From Session 3 onward this becomes
    /// per-session ("WardenIpc-{sessionId}") once the service tracks multiple logged-in
    /// sessions; a single shared pipe is fine while there's exactly one user-agent.
    /// </summary>
    public const string Default = "WardenIpc";
}
