using System.Collections.Generic;

namespace Warden.Core;

/// <summary>
/// Thread-safe, in-memory <see cref="ICommandStore"/>. A single lock around a plain
/// Dictionary is sufficient at v0.1-core's scale: every mutation here is a
/// read-modify-write (check current status, decide legal/no-op/illegal, write new
/// state), so a lock-free structure like ConcurrentDictionary wouldn't remove the need
/// for synchronization anyway — it would just move the race into caller code.
///
/// State does not survive a process restart. That's a deliberate v0.1-core scope
/// decision (see DESIGN.md) — PostgreSQL replaces this behind the same interface in
/// v0.2-mvp without touching Core or the control-plane orchestration logic.
/// </summary>
public sealed class InMemoryCommandStore : ICommandStore
{
    private readonly object _gate = new();
    private readonly Dictionary<CommandId, Command> _commands = new();

    public Command Add(Command command)
    {
        lock (_gate)
        {
            if (!_commands.TryAdd(command.Id, command))
            {
                throw new InvalidOperationException($"Command {command.Id} already exists.");
            }

            return command;
        }
    }

    public Command? Get(CommandId id)
    {
        lock (_gate)
        {
            return _commands.TryGetValue(id, out var command) ? command : null;
        }
    }

    public IReadOnlyList<Command> GetInFlight(DeviceId deviceId)
    {
        lock (_gate)
        {
            return _commands.Values
                .Where(c => c.DeviceId == deviceId && !CommandStatusTransitions.IsTerminal(c.Status))
                .ToList();
        }
    }

    public Command MarkDelivered(CommandId id, DateTimeOffset ackDeadline) =>
        Transition(id, CommandStatus.Delivered, current => current with
        {
            Status = CommandStatus.Delivered,
            Attempts = current.Attempts + 1,
            AckDeadline = ackDeadline
        });

    public Command MarkAcked(CommandId id, DateTimeOffset ackedAt) =>
        Transition(id, CommandStatus.Acked, current => current with
        {
            Status = CommandStatus.Acked,
            AckedAt = ackedAt
        });

    public Command MarkFailed(CommandId id) =>
        Transition(id, CommandStatus.Failed, current => current with
        {
            Status = CommandStatus.Failed
        });

    public Command MarkPendingForRedelivery(CommandId id) =>
        Transition(id, CommandStatus.Pending, current => current with
        {
            Status = CommandStatus.Pending,
            AckDeadline = null
        });

    public IReadOnlyList<Command> GetDeliveredPastDeadline(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _commands.Values
                .Where(c => c.Status == CommandStatus.Delivered
                    && c.AckDeadline.HasValue
                    && c.AckDeadline.Value <= now)
                .ToList();
        }
    }

    /// <summary>
    /// Core guarded-transition logic shared by every Mark* method: look up the current
    /// command, decide whether the requested move is a no-op (return unchanged), legal
    /// (apply and persist), or illegal (throw) — all under one lock so a concurrent
    /// transition on the same command can't interleave with this decision.
    /// </summary>
    private Command Transition(CommandId id, CommandStatus to, Func<Command, Command> apply)
    {
        lock (_gate)
        {
            if (!_commands.TryGetValue(id, out var current))
            {
                throw new KeyNotFoundException($"Command {id} does not exist.");
            }

            if (CommandStatusTransitions.IsNoOp(current.Status, to))
            {
                // Duplicate ack, duplicate fail, duplicate delivered-redelivery-of-same-attempt:
                // already in the target state, so this is a safe no-op. Return the existing
                // record unchanged rather than re-applying (e.g. we must NOT bump Attempts
                // again or overwrite AckedAt on a duplicate ack).
                return current;
            }

            if (!CommandStatusTransitions.IsLegal(current.Status, to))
            {
                throw new InvalidCommandTransitionException(current.Status, to);
            }

            var updated = apply(current);
            _commands[id] = updated;
            return updated;
        }
    }
}