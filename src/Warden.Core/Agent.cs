namespace Warden.Core;

/// <summary>
/// A simulated device. Holds local mutable ActualState and drives the reconciliation
/// loop: register once, then each cycle reports actual state, receives any newly-issued
/// commands, and applies them.
///
/// The load-bearing piece is <see cref="_applied"/>: a set of command ids this agent has
/// already applied. On receiving a command whose id is already in the set, the agent
/// skips the mutation but still acks — the ack itself might be what got lost the first
/// time, so refusing to re-ack would just move the hang somewhere else. This is the
/// entire mechanism behind "at-least-once delivery + idempotent apply": the control
/// plane is free to redeliver without ceremony, because re-application is provably a
/// no-op here.
/// </summary>
public sealed class Agent
{
    private readonly DeviceId _id;
    private readonly string _hostname;
    private readonly IControlPlaneClient _client;
    private readonly HashSet<CommandId> _applied = new();

    private ActualState _actual;

    public Agent(DeviceId id, string hostname, IControlPlaneClient client, ActualState? initialActual = null)
    {
        _id = id;
        _hostname = hostname;
        _client = client;
        _actual = initialActual ?? ActualState.Empty;
    }

    /// <summary>The device's current locally-held actual state, for tests/inspection.</summary>
    public ActualState Actual => _actual;

    /// <summary>Command ids this agent has applied at least once. Exposed for tests only.</summary>
    public IReadOnlySet<CommandId> AppliedCommandIds => _applied;

    /// <summary>Registers this device with the control plane. Safe to call repeatedly.</summary>
    public void Register() => _client.Register(_id, _hostname);

    /// <summary>
    /// Runs one full reconciliation cycle: report actual state, receive any newly-issued
    /// commands, apply each idempotently, and ack. Returns the commands that were
    /// received this cycle (for tests/observability) — an already-compliant device with
    /// nothing in flight will typically receive none.
    /// </summary>
    public IReadOnlyList<Command> RunCycle()
    {
        var commands = _client.ReportState(_id, _actual);

        foreach (var command in commands)
        {
            Apply(command);
        }

        return commands;
    }

    /// <summary>
    /// Applies a single command idempotently: marks it Delivered, mutates local state
    /// only if this command id hasn't been applied before, then acks. Public so a test
    /// can simulate "the same command delivered twice" without going through two full
    /// ReportState round-trips.
    /// </summary>
    public void Apply(Command command)
    {
        _client.MarkDelivered(command.Id);

        if (_applied.Add(command.Id))
        {
            _actual = ApplySetting(_actual, command.Action);
        }
        // else: already applied this exact command id before. Skip the mutation —
        // this is the duplicate-delivery case — but still fall through to ack below,
        // since the ack (not the mutation) may be what the control plane is missing.

        _client.Ack(command.Id);
    }

    /// <summary>
    /// Parses a "set:{key}={value}" action and returns a new ActualState with that
    /// setting applied. Mirrors the action-string convention Reconciler.Diff produces
    /// (Session 2) — deliberately just string parsing, since the domain isn't the point.
    /// </summary>
    private static ActualState ApplySetting(ActualState current, string action)
    {
        const string prefix = "set:";
        if (!action.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unrecognized command action: '{action}'");
        }

        var withoutPrefix = action[prefix.Length..];
        var equalsIndex = withoutPrefix.IndexOf('=');
        if (equalsIndex < 0)
        {
            throw new InvalidOperationException($"Unrecognized command action: '{action}'");
        }

        var key = withoutPrefix[..equalsIndex];
        var value = withoutPrefix[(equalsIndex + 1)..];

        var updated = new Dictionary<string, string>(current.Settings) { [key] = value };
        return new ActualState(updated);
    }
}