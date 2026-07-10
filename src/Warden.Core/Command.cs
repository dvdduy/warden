namespace Warden.Core;

/// <summary>
/// A single instruction issued by the control plane to close a gap between desired
/// and actual state on one device. Immutable — state transitions produce a new Command
/// via `with`, they never mutate in place. The store (Session 3) is the authority that
/// decides which version is current.
/// </summary>
public sealed record Command(
    CommandId Id,
    DeviceId DeviceId,
    string Action,              // e.g. "set:featureX=on" — deliberately just a string for v0.1-core
    CommandStatus Status,
    int Attempts,
    DateTimeOffset IssuedAt,
    DateTimeOffset? AckDeadline,
    DateTimeOffset? AckedAt);