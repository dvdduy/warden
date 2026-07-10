using Warden.Core;

namespace Warden.ControlPlane;

/// <summary>
/// v0.1-core's implementation of the IControlPlaneClient seam: a direct in-process call
/// into <see cref="ControlPlane"/>, no serialization, no network. This is what proves the
/// delivery-guarantee logic is correct before a transport is involved at all — v0.2-mvp
/// replaces this class with a REST client behind the exact same interface, and neither
/// Warden.Core nor Warden.Agent changes.
/// </summary>
public sealed class InProcessControlPlaneClient : Core.IControlPlaneClient
{
    /// <summary>
    /// Default ack deadline: how long a command has to be acked before the ack-timeout
    /// sweeper considers it overdue. Owned here — the client seam, not the agent —
    /// because in a real transport this is a control-plane policy, not something the
    /// agent decides. Overridable per-instance (e.g. Warden.Demo uses a short deadline
    /// with a manually-advanced clock so a redeliver-then-fail demo doesn't need to
    /// wait 30 real seconds).
    /// </summary>
    public static readonly TimeSpan DefaultAckDeadline = TimeSpan.FromSeconds(30);

    private readonly ControlPlane _controlPlane;
    private readonly TimeSpan _ackDeadline;

    public InProcessControlPlaneClient(ControlPlane controlPlane, TimeSpan? ackDeadline = null)
    {
        _controlPlane = controlPlane;
        _ackDeadline = ackDeadline ?? DefaultAckDeadline;
    }

    public Device Register(DeviceId id, string hostname) =>
        _controlPlane.RegisterDevice(id, hostname);

    public IReadOnlyList<Command> ReportState(DeviceId id, ActualState actual) =>
        _controlPlane.ReportStateAndGetNewCommands(id, actual);

    public Command MarkDelivered(CommandId commandId) =>
        _controlPlane.MarkDelivered(commandId, _ackDeadline);

    public Command Ack(CommandId commandId) =>
        _controlPlane.Ack(commandId);
}