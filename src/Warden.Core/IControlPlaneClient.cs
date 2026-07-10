namespace Warden.Core;

/// <summary>
/// The seam between an agent and however the control plane is actually reached.
/// Warden.Agent depends only on this interface, never on Warden.ControlPlane directly —
/// that's what makes the transport swappable. v0.1-core wires an in-process
/// implementation (Warden.ControlPlane.InProcessControlPlaneClient) straight into the
/// orchestrator with a plain method call. v0.2-mvp swaps in a REST-based implementation
/// that serializes these same calls over HTTP. Nothing in Warden.Core or Warden.Agent
/// needs to change either time — that's the entire point of the seam.
/// </summary>
public interface IControlPlaneClient
{
    /// <summary>Registers (or re-registers) the calling device.</summary>
    Device Register(DeviceId id, string hostname);

    /// <summary>
    /// Reports the device's current actual state and, in the same round-trip, receives
    /// any newly-issued commands to close gaps. Mirrors the real protocol's
    /// report-then-receive shape even though v0.1-core has no separate "fetch desired
    /// state" step — the agent doesn't need desired state directly, only the commands
    /// that result from diffing it.
    /// </summary>
    IReadOnlyList<Command> ReportState(DeviceId id, ActualState actual);

    /// <summary>
    /// Called when the agent is about to apply a command, so the control plane can mark
    /// it Delivered. Kept as an explicit step (distinct from ReportState) because in a
    /// real transport, "the control plane sent it" and "the agent received/applied it"
    /// are genuinely different moments that can fail independently.
    /// </summary>
    Command MarkDelivered(CommandId commandId);

    /// <summary>Acknowledges that a command was applied (or already had been — see dedup).</summary>
    Command Ack(CommandId commandId);
}