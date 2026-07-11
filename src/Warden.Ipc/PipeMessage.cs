namespace Warden.Ipc;

using System.Text.Json;

/// <summary>
/// The wire message for the service &lt;-&gt; user-agent named pipe. Small and versioned by
/// <see cref="Type"/> string so new message kinds don't require a protocol version bump.
/// </summary>
public sealed record PipeMessage(string Type, string? Payload = null)
{
    public static readonly PipeMessage Ping = new("Ping");
    public static readonly PipeMessage Pong = new("Pong");

    public static PipeMessage ComplianceChanged(string rule, string status) =>
        new("ComplianceChanged", JsonSerializer.Serialize(new ComplianceChangedPayload(rule, status)));

    public ComplianceChangedPayload? TryGetComplianceChangedPayload()
    {
        if (Type != "ComplianceChanged" || string.IsNullOrWhiteSpace(Payload))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ComplianceChangedPayload>(Payload);
    }
}

public sealed record ComplianceChangedPayload(string Rule, string Status);
