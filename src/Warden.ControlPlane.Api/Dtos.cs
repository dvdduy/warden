using Warden.Core;

namespace Warden.ControlPlane.Api;

/// <summary>
/// Wire DTOs for the REST transport. Deliberately separate from the Warden.Core domain
/// records (DeviceId, Command, ...) -- the wire shape is an HTTP/JSON concern, not a
/// domain one, and keeping them apart means a future JSON contract change never touches
/// Warden.Core. Warden.Agent's RestControlPlaneClient defines its own copies of these
/// shapes rather than sharing a project reference, since the client and server are
/// deliberately allowed to evolve independently once real versioning matters -- for
/// v0.2-mvp's scope, duplicating four small records is cheaper than a shared contracts
/// project neither side asked for.
/// </summary>
public sealed record EnrollRequest(string DeviceId, string Hostname);

public sealed record ReportStateRequest(Dictionary<string, string> Settings);

public sealed record DeviceDto(string Id, string Hostname, Dictionary<string, string> Actual, DateTimeOffset LastSeen)
{
    public static DeviceDto From(Device device) => new(
        device.Id.Value, device.Hostname, new Dictionary<string, string>(device.Actual.Settings), device.LastSeen);
}

public sealed record CommandDto(
    string Id,
    string DeviceId,
    string Action,
    string Status,
    int Attempts,
    DateTimeOffset IssuedAt,
    DateTimeOffset? AckDeadline,
    DateTimeOffset? AckedAt)
{
    public static CommandDto From(Command command) => new(
        command.Id.Value, command.DeviceId.Value, command.Action, command.Status.ToString(),
        command.Attempts, command.IssuedAt, command.AckDeadline, command.AckedAt);
}
