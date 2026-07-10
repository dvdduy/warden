namespace Warden.Core;

/// <summary>
/// The authority on command lifecycle state. Reconciler decides *what* command should
/// exist for a gap; the store decides *what state a command is actually in* and is the
/// only thing allowed to mutate that state. Every transition is routed through here and
/// guarded by <see cref="CommandStatusTransitions"/> so illegal moves are rejected and
/// duplicate/no-op transitions (e.g. a repeated ack) collapse safely instead of
/// corrupting state.
///
/// Implementations must be safe for concurrent use — many simulated agents (and, later,
/// the sweeper) hit the same store at once.
/// </summary>
public interface ICommandStore
{
    /// <summary>Adds a newly issued command. Throws if a command with this Id already exists.</summary>
    Command Add(Command command);

    /// <summary>Looks up a command by id, or null if it doesn't exist.</summary>
    Command? Get(CommandId id);

    /// <summary>All commands currently Pending or Delivered for a device — i.e. not yet terminal.</summary>
    IReadOnlyList<Command> GetInFlight(DeviceId deviceId);

    /// <summary>
    /// Transitions a command to Delivered, incrementing Attempts and setting AckDeadline.
    /// Legal from Pending. A no-op (returns current state unchanged) if already Delivered.
    /// Throws <see cref="InvalidCommandTransitionException"/> for any other current status.
    /// </summary>
    Command MarkDelivered(CommandId id, DateTimeOffset ackDeadline);

    /// <summary>
    /// Transitions a command to Acked. Legal from Delivered. A no-op if already Acked
    /// (this is what makes duplicate acks safe). Throws for any other current status
    /// (e.g. acking a Failed command), since Acked/Failed are terminal.
    /// </summary>
    Command MarkAcked(CommandId id, DateTimeOffset ackedAt);

    /// <summary>
    /// Transitions a command to Failed. Legal from Delivered. A no-op if already Failed.
    /// Throws for any other current status.
    /// </summary>
    Command MarkFailed(CommandId id);
}

/// <summary>
/// Thrown when a caller attempts an illegal command-status transition (e.g. acking a
/// Failed command). Distinct from a no-op — a no-op succeeds silently, an illegal
/// transition is a bug in the caller and should be surfaced, not swallowed.
/// </summary>
public sealed class InvalidCommandTransitionException(CommandStatus from, CommandStatus to)
    : InvalidOperationException($"Cannot transition command from {from} to {to}.")
{
    public CommandStatus From { get; } = from;
    public CommandStatus To { get; } = to;
}