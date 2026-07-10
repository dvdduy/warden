namespace Warden.Core;

/// <summary>
/// The lifecycle of a single command. Modelled as an enum with guarded transitions
/// (see <see cref="CommandStatusTransitions"/>) rather than a set of independent booleans,
/// so that states like "both Acked and Failed" are unrepresentable rather than merely
/// disallowed by convention.
///
/// Legal transitions:
///   Pending   -> Delivered              (control plane serves it to the agent)
///   Delivered -> Acked                  (agent confirms application)
///   Delivered -> Pending                (ack-timeout sweeper redelivers, Session 5)
///   Delivered -> Failed                 (ack-timeout sweeper exhausts retries, Session 5)
///
/// Acked and Failed are terminal: no transition leaves them. Re-applying the *same*
/// transition to a terminal state (e.g. acking an already-Acked command) is a no-op,
/// not an error — this is what makes duplicate acks safe (Session 3).
/// </summary>
public enum CommandStatus
{
    Pending,
    Delivered,
    Acked,
    Failed
}

/// <summary>
/// Single source of truth for which CommandStatus transitions are legal. The command
/// store (Session 3) uses this to guard every mutation so illegal transitions are
/// rejected rather than silently corrupting state, and duplicate/no-op transitions
/// are recognized rather than treated as errors.
/// </summary>
public static class CommandStatusTransitions
{
    /// <summary>
    /// True if moving from <paramref name="from"/> to <paramref name="to"/> is a legal,
    /// state-changing transition. Does NOT cover the "already in target state" no-op
    /// case — callers should check that separately (see IsNoOp).
    /// </summary>
    public static bool IsLegal(CommandStatus from, CommandStatus to) => (from, to) switch
    {
        (CommandStatus.Pending, CommandStatus.Delivered) => true,
        (CommandStatus.Delivered, CommandStatus.Acked) => true,
        (CommandStatus.Delivered, CommandStatus.Pending) => true,
        (CommandStatus.Delivered, CommandStatus.Failed) => true,
        _ => false
    };

    /// <summary>
    /// True if the command is already in a state where reapplying <paramref name="to"/>
    /// is a no-op that should be silently ignored (e.g. acking an already-Acked command),
    /// rather than rejected as illegal.
    /// </summary>
    public static bool IsNoOp(CommandStatus current, CommandStatus to) => current == to;

    /// <summary>Acked and Failed are terminal — no transition leaves them.</summary>
    public static bool IsTerminal(CommandStatus status) =>
        status is CommandStatus.Acked or CommandStatus.Failed;
}