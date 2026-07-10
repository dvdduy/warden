using Warden.Core;

namespace Warden.ControlPlane;

/// <summary>
/// Orchestrates the control-plane side of one reconciliation cycle. This class contains
/// no domain logic of its own — it is glue: it calls Reconciler.Diff (Session 2) for the
/// decision, ICommandStore (Session 3) for command lifecycle, and IDeviceRepository for
/// device/desired-state storage. Keeping it this thin is deliberate: the interesting
/// logic stays pure and unit-testable in Warden.Core; this class is what a REST
/// controller would call in v0.2-mvp.
/// </summary>
public sealed class ControlPlane
{
    private readonly IDeviceRepository _devices;
    private readonly ICommandStore _commands;
    private readonly IClock _clock;
    private readonly AckTimeoutSweeper _sweeper;

    public ControlPlane(
        IDeviceRepository devices,
        ICommandStore commands,
        IClock clock,
        int maxDeliveryAttempts = AckTimeoutSweeper.DefaultMaxAttempts)
    {
        _devices = devices;
        _commands = commands;
        _clock = clock;
        _sweeper = new AckTimeoutSweeper(commands, clock, maxDeliveryAttempts);
    }

    /// <summary>Registers a device (idempotent — see IDeviceRepository.Register).</summary>
    public Device RegisterDevice(DeviceId id, string hostname) =>
        _devices.Register(id, hostname, _clock);

    /// <summary>Sets what a device's desired state should be. Used by tests / a future admin API.</summary>
    public void SetDesiredState(DeviceId id, DesiredState desired) =>
        _devices.SetDesiredState(id, desired);

    /// <summary>
    /// The core cycle: record the device's self-reported actual state, supersede any
    /// in-flight command whose target no longer matches current desired state, then diff
    /// the remaining gaps (respecting what's still in flight) and hand back everything
    /// the agent needs to act on this cycle: commands still sitting Pending (redelivery —
    /// e.g. the ack-timeout sweeper put them back) plus any newly-issued ones. Does NOT
    /// mark anything Delivered — that happens when the agent actually receives it (see
    /// Agent.Apply), keeping "a command exists" and "a command was delivered" as distinct,
    /// observable events.
    /// </summary>
    public IReadOnlyList<Command> ReportStateAndGetNewCommands(DeviceId id, ActualState actual)
    {
        _devices.ReportActualState(id, actual, _clock);

        var desired = _devices.GetDesiredState(id);
        var inFlight = _commands.GetInFlight(id);

        var superseded = Reconciler.FindSuperseded(desired, inFlight);
        foreach (var commandId in superseded)
        {
            // Terminal Failed, same as an exhausted-retries command — v0.1-core's
            // CommandStatus deliberately stays at four states (see CLAUDE.md), so
            // "superseded by newer desired state" and "gave up after retries" share
            // the same terminal status rather than growing a fifth.
            _commands.MarkFailed(commandId);
        }

        var stillInFlight = superseded.Count == 0
            ? inFlight
            : inFlight.Where(c => !superseded.Contains(c.Id)).ToList();

        var pendingRedelivery = stillInFlight.Where(c => c.Status == CommandStatus.Pending).ToList();

        var newCommands = Reconciler.Diff(id, desired, actual, stillInFlight, _clock);
        foreach (var command in newCommands)
        {
            _commands.Add(command);
        }

        return pendingRedelivery.Concat(newCommands).ToList();
    }

    /// <summary>
    /// Runs one ack-timeout sweep across every device's Delivered commands: redelivers
    /// (moves back to Pending) while attempts remain, else marks Failed. Driven by the
    /// injected IClock, so tests advance a FakeClock instead of waiting for real time —
    /// see AckTimeoutSweeper.
    /// </summary>
    public IReadOnlyList<Command> SweepAckTimeouts() => _sweeper.SweepOnce();

    /// <summary>
    /// Marks a command Delivered (the agent is now being handed it) and returns the
    /// updated record. Guarded/idempotent via ICommandStore — delivering an
    /// already-Delivered command again is a no-op, not a double-increment.
    /// </summary>
    public Command MarkDelivered(CommandId commandId, TimeSpan ackDeadlineFromNow) =>
        _commands.MarkDelivered(commandId, _clock.UtcNow + ackDeadlineFromNow);

    /// <summary>
    /// Records the agent's acknowledgement that a command was applied. Idempotent —
    /// duplicate acks collapse to a no-op via ICommandStore (Session 3).
    /// </summary>
    public Command Ack(CommandId commandId) =>
        _commands.MarkAcked(commandId, _clock.UtcNow);

    /// <summary>All commands currently Pending or Delivered for a device.</summary>
    public IReadOnlyList<Command> GetInFlightCommands(DeviceId id) =>
        _commands.GetInFlight(id);
}