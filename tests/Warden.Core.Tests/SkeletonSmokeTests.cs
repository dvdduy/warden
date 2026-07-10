using Warden.Core;
using Xunit;

namespace Warden.Core.Tests;

public class SkeletonSmokeTests
{
    [Fact]
    public void CommandId_NewId_generates_unique_ids()
    {
        var a = CommandId.NewId();
        var b = CommandId.NewId();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeviceId_has_value_equality()
    {
        var a = new DeviceId("dev_1");
        var b = new DeviceId("dev_1");

        Assert.Equal(a, b);
    }

    [Fact]
    public void FakeClock_only_advances_when_told_to()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);

        Assert.Equal(start, clock.UtcNow);

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(start + TimeSpan.FromSeconds(30), clock.UtcNow);
    }

    [Theory]
    [InlineData(CommandStatus.Pending, CommandStatus.Delivered, true)]
    [InlineData(CommandStatus.Delivered, CommandStatus.Acked, true)]
    [InlineData(CommandStatus.Delivered, CommandStatus.Pending, true)]
    [InlineData(CommandStatus.Delivered, CommandStatus.Failed, true)]
    [InlineData(CommandStatus.Acked, CommandStatus.Delivered, false)]
    [InlineData(CommandStatus.Failed, CommandStatus.Acked, false)]
    [InlineData(CommandStatus.Pending, CommandStatus.Acked, false)]
    public void CommandStatusTransitions_IsLegal_matches_the_state_machine(
        CommandStatus from, CommandStatus to, bool expectedLegal)
    {
        Assert.Equal(expectedLegal, CommandStatusTransitions.IsLegal(from, to));
    }

    [Fact]
    public void CommandStatusTransitions_terminal_states_are_Acked_and_Failed()
    {
        Assert.True(CommandStatusTransitions.IsTerminal(CommandStatus.Acked));
        Assert.True(CommandStatusTransitions.IsTerminal(CommandStatus.Failed));
        Assert.False(CommandStatusTransitions.IsTerminal(CommandStatus.Pending));
        Assert.False(CommandStatusTransitions.IsTerminal(CommandStatus.Delivered));
    }

    [Fact]
    public void Command_record_supports_with_expressions_for_state_transitions()
    {
        var issued = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var command = new Command(
            Id: CommandId.NewId(),
            DeviceId: new DeviceId("dev_1"),
            Action: "set:featureX=on",
            Status: CommandStatus.Pending,
            Attempts: 0,
            IssuedAt: issued,
            AckDeadline: null,
            AckedAt: null);

        var delivered = command with
        {
            Status = CommandStatus.Delivered,
            AckDeadline = issued + TimeSpan.FromSeconds(30)
        };

        // Original is untouched — records are immutable.
        Assert.Equal(CommandStatus.Pending, command.Status);
        Assert.Equal(CommandStatus.Delivered, delivered.Status);
        Assert.Equal(command.Id, delivered.Id);
    }
}