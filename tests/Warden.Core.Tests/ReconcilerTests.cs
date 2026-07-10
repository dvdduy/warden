using Warden.Core;
using Xunit;

namespace Warden.Core.Tests;

public class ReconcilerTests
{
    private static readonly DeviceId Device1 = new("dev_1");

    private static DesiredState Desired(params (string Key, string Value)[] settings) =>
        new(settings.ToDictionary(s => s.Key, s => s.Value));

    private static ActualState Actual(params (string Key, string Value)[] settings) =>
        new(settings.ToDictionary(s => s.Key, s => s.Value));

    [Fact]
    public void Gap_produces_one_command()
    {
        // Arrange
        var desired = Desired(("featureX", "on"));
        var actual = Actual(("featureX", "off"));
        var clock = new FakeClock();

        // Act
        var commands = Reconciler.Diff(Device1, desired, actual, inFlight: Array.Empty<Command>(), clock);

        // Assert
        var command = Assert.Single(commands);
        Assert.Equal(Device1, command.DeviceId);
        Assert.Equal("set:featureX=on", command.Action);
        Assert.Equal(CommandStatus.Pending, command.Status);
        Assert.Equal(0, command.Attempts);
        Assert.Equal(clock.UtcNow, command.IssuedAt);
    }

    [Fact]
    public void Setting_missing_from_actual_is_a_gap()
    {
        // Arrange
        var desired = Desired(("featureX", "on"));
        var actual = ActualState.Empty; // device has never reported this setting at all
        var clock = new FakeClock();

        // Act
        var commands = Reconciler.Diff(Device1, desired, actual, inFlight: Array.Empty<Command>(), clock);

        // Assert
        var command = Assert.Single(commands);
        Assert.Equal("set:featureX=on", command.Action);
    }

    [Fact]
    public void Compliant_device_produces_no_commands()
    {
        // Arrange
        var desired = Desired(("featureX", "on"));
        var actual = Actual(("featureX", "on"));
        var clock = new FakeClock();

        // Act
        var commands = Reconciler.Diff(Device1, desired, actual, inFlight: Array.Empty<Command>(), clock);

        // Assert
        Assert.Empty(commands);
    }

    [Fact]
    public void Gap_with_in_flight_command_produces_no_new_command()
    {
        // Arrange
        var desired = Desired(("featureX", "on"));
        var actual = Actual(("featureX", "off"));
        var clock = new FakeClock();
        var existingCommand = new Command(
            Id: CommandId.NewId(),
            DeviceId: Device1,
            Action: "set:featureX=on",
            Status: CommandStatus.Delivered,
            Attempts: 1,
            IssuedAt: clock.UtcNow,
            AckDeadline: clock.UtcNow.AddSeconds(30),
            AckedAt: null);

        // Act
        var commands = Reconciler.Diff(Device1, desired, actual, inFlight: new[] { existingCommand }, clock);

        // Assert
        Assert.Empty(commands);
    }

    [Fact]
    public void In_flight_command_for_different_setting_does_not_block_this_gap()
    {
        // Arrange
        var desired = Desired(("featureX", "on"), ("featureY", "on"));
        var actual = Actual(("featureX", "off"), ("featureY", "off"));
        var clock = new FakeClock();
        var inFlightForY = new Command(
            Id: CommandId.NewId(),
            DeviceId: Device1,
            Action: "set:featureY=on",
            Status: CommandStatus.Pending,
            Attempts: 0,
            IssuedAt: clock.UtcNow,
            AckDeadline: null,
            AckedAt: null);

        // Act
        var commands = Reconciler.Diff(Device1, desired, actual, inFlight: new[] { inFlightForY }, clock);

        // Assert
        var command = Assert.Single(commands);
        Assert.Equal("set:featureX=on", command.Action);
    }

    [Fact]
    public void Multiple_gaps_produce_one_command_each()
    {
        // Arrange
        var desired = Desired(("featureX", "on"), ("featureY", "on"), ("featureZ", "off"));
        var actual = Actual(("featureX", "off"), ("featureY", "off"), ("featureZ", "off"));
        var clock = new FakeClock();

        // Act
        var commands = Reconciler.Diff(Device1, desired, actual, inFlight: Array.Empty<Command>(), clock);

        // Assert
        Assert.Equal(2, commands.Count); // featureZ is already compliant
        Assert.Contains(commands, c => c.Action == "set:featureX=on");
        Assert.Contains(commands, c => c.Action == "set:featureY=on");
    }

    [Fact]
    public void Diff_is_deterministic_across_repeated_calls_with_same_inputs()
    {
        // Arrange
        var desired = Desired(("featureX", "on"));
        var actual = Actual(("featureX", "off"));
        var clock = new FakeClock();

        // Act
        var firstCall = Reconciler.Diff(Device1, desired, actual, inFlight: Array.Empty<Command>(), clock);
        var secondCall = Reconciler.Diff(Device1, desired, actual, inFlight: Array.Empty<Command>(), clock);

        // Assert
        // Same inputs -> same shape of output every time. Ids will differ since each
        // call mints a fresh CommandId, which is expected — the *decision* to issue
        // a command for this gap is what must be deterministic, not the id.
        Assert.Single(firstCall);
        Assert.Single(secondCall);
        Assert.Equal(firstCall[0].Action, secondCall[0].Action);
        Assert.Equal(firstCall[0].DeviceId, secondCall[0].DeviceId);
    }

    [Fact]
    public void Diff_trusts_inFlight_and_does_not_filter_terminal_commands_itself()
    {
        // This documents a caller contract rather than testing Diff's internal logic:
        // Diff does not filter inFlight by status. If a caller mistakenly includes a
        // Failed or Acked command, Diff has no way to know it's terminal and will
        // incorrectly suppress a legitimate new command for a real gap. The control
        // plane (Session 3/4) is responsible for only ever passing non-terminal
        // (Pending/Delivered) commands into inFlight.

        // Arrange
        var desired = Desired(("featureX", "on"));
        var actual = Actual(("featureX", "off"));
        var clock = new FakeClock();
        var failedCommand = new Command(
            Id: CommandId.NewId(),
            DeviceId: Device1,
            Action: "set:featureX=on",
            Status: CommandStatus.Failed,
            Attempts: 3,
            IssuedAt: clock.UtcNow,
            AckDeadline: clock.UtcNow.AddSeconds(30),
            AckedAt: null);

        // Act
        var commands = Reconciler.Diff(Device1, desired, actual, inFlight: new[] { failedCommand }, clock);

        // Assert
        Assert.Empty(commands); // demonstrates why the caller-side filter matters
    }
}