namespace Warden.Core;

/// <summary>
/// The reconciliation engine. Diff is a pure function: deterministic, no I/O, no side
/// effects. Given the same desired/actual/inFlight inputs it always produces the same
/// commands — which makes it idempotent by construction, not by convention, and testable
/// with nothing but plain data in, plain data out.
///
/// This is the single most load-bearing piece of logic in the system: the control plane
/// is just infrastructure that calls this and applies its output.
/// </summary>
public static class Reconciler
{
    /// <summary>
    /// Computes the commands needed to close the gap between desired and actual state
    /// for one device, respecting the invariant that at most one in-flight command may
    /// exist per gap.
    ///
    /// A "gap" is a setting key in <paramref name="desired"/> whose value doesn't match
    /// <paramref name="actual"/> (including keys entirely missing from actual). Settings
    /// present only in actual are ignored — removal is out of scope for v0.1-core.
    ///
    /// Returns an empty list when the device is already fully compliant, and skips any
    /// gap that already has a Pending or Delivered command in <paramref name="inFlight"/>
    /// — this is what prevents the reconciler from re-issuing a command every cycle while
    /// waiting for an ack.
    /// </summary>
    /// <param name="deviceId">The device being reconciled.</param>
    /// <param name="desired">What the device should look like.</param>
    /// <param name="actual">What the device currently reports looking like.</param>
    /// <param name="inFlight">
    /// Commands for this device currently Pending or Delivered (not yet Acked/Failed).
    /// Callers are responsible for filtering this to the relevant device and to
    /// non-terminal statuses — Diff trusts what it's given and does not re-filter.
    /// </param>
    /// <param name="clock">Used to timestamp any newly issued command.</param>
    public static IReadOnlyList<Command> Diff(
        DeviceId deviceId,
        DesiredState desired,
        ActualState actual,
        IReadOnlyList<Command> inFlight,
        IClock clock)
    {
        var gaps = FindGaps(desired, actual);
        if (gaps.Count == 0)
        {
            return Array.Empty<Command>();
        }

        var settingsWithInFlightCommand = InFlightSettingKeys(inFlight);

        var commands = new List<Command>();
        foreach (var (key, desiredValue) in gaps)
        {
            if (settingsWithInFlightCommand.Contains(key))
            {
                // A command for this exact gap is already Pending/Delivered.
                // Emitting another would risk a command storm; the existing
                // one will either get acked or, later, redelivered/failed by
                // the sweeper (Session 5) — not duplicated here.
                continue;
            }

            commands.Add(new Command(
                Id: CommandId.NewId(),
                DeviceId: deviceId,
                Action: $"set:{key}={desiredValue}",
                Status: CommandStatus.Pending,
                Attempts: 0,
                IssuedAt: clock.UtcNow,
                AckDeadline: null,
                AckedAt: null));
        }

        return commands;
    }

    /// <summary>
    /// Setting keys in desired whose value differs from actual (or is missing from actual).
    /// </summary>
    private static List<(string Key, string DesiredValue)> FindGaps(DesiredState desired, ActualState actual)
    {
        var gaps = new List<(string, string)>();

        foreach (var (key, desiredValue) in desired.Settings)
        {
            var isCompliant = actual.Settings.TryGetValue(key, out var actualValue)
                && actualValue == desiredValue;

            if (!isCompliant)
            {
                gaps.Add((key, desiredValue));
            }
        }

        return gaps;
    }

    /// <summary>
    /// Extracts the setting key each in-flight command targets, by parsing the
    /// "set:{key}={value}" action convention. This coupling to the action string
    /// format is deliberately contained to this one method.
    /// </summary>
    private static HashSet<string> InFlightSettingKeys(IReadOnlyList<Command> inFlight)
    {
        var keys = new HashSet<string>();

        foreach (var command in inFlight)
        {
            var key = ParseSettingKey(command.Action);
            if (key is not null)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static string? ParseSettingKey(string action)
    {
        // Expected shape: "set:{key}={value}"
        const string prefix = "set:";
        if (!action.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var withoutPrefix = action[prefix.Length..];
        var equalsIndex = withoutPrefix.IndexOf('=');
        return equalsIndex < 0 ? null : withoutPrefix[..equalsIndex];
    }
}