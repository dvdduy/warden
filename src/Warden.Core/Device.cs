namespace Warden.Core;

/// <summary>
/// A registered device as known to the control plane: its last-reported actual state
/// and when it was last seen. The desired state is NOT stored on the device — it's
/// looked up separately, since it's set by policy, not by the device itself.
/// </summary>
public sealed record Device(
    DeviceId Id,
    string Hostname,
    ActualState Actual,
    DateTimeOffset LastSeen);