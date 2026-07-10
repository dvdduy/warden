using Warden.Core;

namespace Warden.ControlPlane;

/// <summary>
/// Bounded retry for commands the agent never acked. Runs fleet-wide (not scoped to one
/// device) against ICommandStore.GetDeliveredPastDeadline, driven entirely by IClock —
/// tests advance a FakeClock rather than sleeping for real deadlines. For each overdue
/// Delivered command: redeliver (back to Pending) while Attempts is still below the
/// limit, otherwise mark it Failed. A Failed command is terminal — the gap it targeted
/// gets picked up fresh by Reconciler.Diff on the device's next report cycle, issuing a
/// brand new command rather than resurrecting the failed one.
/// </summary>
public sealed class AckTimeoutSweeper
{
    public const int DefaultMaxAttempts = 3;

    private readonly ICommandStore _commands;
    private readonly IClock _clock;
    private readonly int _maxAttempts;

    public AckTimeoutSweeper(ICommandStore commands, IClock clock, int maxAttempts = DefaultMaxAttempts)
    {
        _commands = commands;
        _clock = clock;
        _maxAttempts = maxAttempts;
    }

    /// <summary>
    /// One sweep pass. Safe to call repeatedly and from multiple callers — every
    /// transition routes through ICommandStore's guarded, idempotent Mark* methods, so a
    /// command already moved by a concurrent sweep just no-ops here.
    /// </summary>
    public IReadOnlyList<Command> SweepOnce()
    {
        var overdue = _commands.GetDeliveredPastDeadline(_clock.UtcNow);
        var results = new List<Command>(overdue.Count);

        foreach (var command in overdue)
        {
            var updated = command.Attempts < _maxAttempts
                ? _commands.MarkPendingForRedelivery(command.Id)
                : _commands.MarkFailed(command.Id);
            results.Add(updated);
        }

        return results;
    }
}
