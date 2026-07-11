namespace Warden.Ipc;

/// <summary>
/// The wire message for the service &lt;-&gt; user-agent named pipe. Small and versioned by
/// <see cref="Type"/> string so new message kinds (e.g. ComplianceChanged in a later session)
/// don't require a protocol version bump.
/// </summary>
public sealed record PipeMessage(string Type, string? Payload = null)
{
    public static readonly PipeMessage Ping = new("Ping");
    public static readonly PipeMessage Pong = new("Pong");
}
