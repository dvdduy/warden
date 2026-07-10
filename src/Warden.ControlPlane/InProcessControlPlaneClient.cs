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
    /// How long a command has to be acked before the ack-timeout sweeper (Session 5)
    /// considers it overdue. Owned here — the client seam, not the agent — because in
    /// a real transport this is a control-plane policy, not something the agent decides.
    /// </summary>
    private static readonly TimeSpan AckDeadline = TimeSpan.FromSeconds(30);

    private readonly ControlPlane _controlPlane;

    public InProcessControlPlaneClient(ControlPlane controlPlane)
    {
        _controlPlane = controlPlane;
    }

    public Device Register(DeviceId id, string hostname) =>
        _controlPlane.RegisterDevice(id, hostname);

    public IReadOnlyList<Command> ReportState(DeviceId id, ActualState actual) =>
        _controlPlane.ReportStateAndGetNewCommands(id, actual);

    public Command MarkDelivered(CommandId commandId) =>
        _controlPlane.MarkDelivered(commandId, AckDeadline);

    public Command Ack(CommandId commandId) =>
        _controlPlane.Ack(commandId);
}