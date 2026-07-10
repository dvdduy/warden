using Warden.Core;
using Xunit;

namespace Warden.Core.Tests;

/// <summary>
/// Reconciler.FindSuperseded: identifying in-flight commands whose target value no
/// longer matches current desired state, because desired state changed after the
/// command was issued but before it was acked. Pure — same shape as Reconciler.Diff.
/// </summary>
public class ReconcilerSupersedeTests
{
    private static readonly DeviceId Device1 = new("dev_1");

    private static DesiredState Desired(params (string Key, string Value)[] settings) =>
        new(settings.ToDictionary(s => s.Key, s => s.Value));

    private static Command InFlight(string action, CommandStatus status = CommandStatus.Delivered) =>
        new(
            Id: CommandId.NewId(),
            DeviceId: Device1,
            Action: action,
            Status: status,
            Attempts: status == CommandStatus.Delivered ? 1 : 0,
            IssuedAt: DateTimeOffset.UtcNow,
            AckDeadline: status == CommandStatus.Delivered ? DateTimeOffset.UtcNow.AddSeconds(30) : null,
            AckedAt: null);

    [Fact]
    public void Command_targeting_the_current_desired_value_is_not_superseded()
    {
        var desired = Desired(("featureX", "on"));
        var command = InFlight("set:featureX=on");

        var superseded = Reconciler.FindSuperseded(desired, new[] { command });

        Assert.Empty(superseded);
    }

    [Fact]
    public void Command_targeting_a_stale_value_is_superseded()
    {
        // Desired changed from "on" to "off" while the command (still targeting "on")
        // was in flight.
        var desired = Desired(("featureX", "off"));
        var command = InFlight("set:featureX=on");

        var superseded = Reconciler.FindSuperseded(desired, new[] { command });

        Assert.Equal(new[] { command.Id }, superseded);
    }

    [Fact]
    public void Superseding_applies_to_Pending_commands_too_not_just_Delivered()
    {
        var desired = Desired(("featureX", "off"));
        var command = InFlight("set:featureX=on", CommandStatus.Pending);

        var superseded = Reconciler.FindSuperseded(desired, new[] { command });

        Assert.Equal(new[] { command.Id }, superseded);
    }

    [Fact]
    public void Command_for_a_key_no_longer_present_in_desired_is_left_alone()
    {
        // Removal is out of scope for v0.1-core (see Reconciler.FindGaps) -- an
        // in-flight command for a key that vanished from desired isn't treated as
        // superseded, since there's no replacement gap to issue.
        var desired = DesiredState.Empty;
        var command = InFlight("set:featureX=on");

        var superseded = Reconciler.FindSuperseded(desired, new[] { command });

        Assert.Empty(superseded);
    }

    [Fact]
    public void Multiple_in_flight_commands_only_stale_ones_are_superseded()
    {
        var desired = Desired(("featureX", "off"), ("featureY", "on"));
        var stale = InFlight("set:featureX=on");
        var current = InFlight("set:featureY=on");

        var superseded = Reconciler.FindSuperseded(desired, new[] { stale, current });

        Assert.Equal(new[] { stale.Id }, superseded);
    }
}
