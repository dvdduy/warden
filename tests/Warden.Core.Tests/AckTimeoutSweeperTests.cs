using Warden.Core;
using Warden.ControlPlane;
using Xunit;

namespace Warden.Core.Tests;

/// <summary>
/// Hard behavior #4: a command that's never acked redelivers a bounded number of times
/// then lands in a visible Failed state -- it never hangs forever. Everything here runs
/// on FakeClock; no real sleeping.
/// </summary>
public class AckTimeoutSweeperTests
{
    private static (Core.Agent agent, ControlPlane.ControlPlane controlPlane, ICommandStore commandStore, FakeClock clock) BuildHarness(
        DeviceId deviceId, int maxAttempts = 2)
    {
        var clock = new FakeClock();
        var deviceRepo = new InMemoryDeviceRepository();
        var commandStore = new InMemoryCommandStore();
        var controlPlane = new ControlPlane.ControlPlane(deviceRepo, commandStore, clock, maxAttempts);
        var client = new InProcessControlPlaneClient(controlPlane);
        var agent = new Core.Agent(deviceId, "test-device", client);
        agent.Register();

        return (agent, controlPlane, commandStore, clock);
    }

    [Fact]
    public void No_ack_before_deadline_redelivers_the_same_command_id()
    {
        var deviceId = new DeviceId("dev_1");
        var (_, controlPlane, commandStore, clock) = BuildHarness(deviceId);

        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureX"] = "on" }));

        // Agent reports state, gets the command back, but crashes before applying/acking
        // it -- MarkDelivered never gets called, so it just sits Pending forever unless
        // something drives it. Simulate the control plane having delivered it directly.
        var issued = Assert.Single(controlPlane.ReportStateAndGetNewCommands(deviceId, ActualState.Empty));
        controlPlane.MarkDelivered(issued.Id, TimeSpan.FromSeconds(30));

        clock.Advance(TimeSpan.FromSeconds(31));
        var swept = controlPlane.SweepAckTimeouts();

        var redelivered = Assert.Single(swept);
        Assert.Equal(issued.Id, redelivered.Id); // same command id -- redelivery, not a new command
        Assert.Equal(CommandStatus.Pending, redelivered.Status);
        Assert.Equal(CommandStatus.Pending, commandStore.Get(issued.Id)!.Status);
    }

    [Fact]
    public void A_redelivered_command_is_handed_back_to_the_agent_on_its_next_cycle()
    {
        // Simulates the ack getting lost in transit: the control plane delivers the
        // command directly (bypassing the agent, standing in for "the agent received it
        // but its ack never arrived"), so it sits Delivered past its deadline.
        var deviceId = new DeviceId("dev_1");
        var (agent, controlPlane, commandStore, clock) = BuildHarness(deviceId, maxAttempts: 2);

        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureY"] = "on" }));

        var issued = Assert.Single(controlPlane.ReportStateAndGetNewCommands(deviceId, ActualState.Empty));
        controlPlane.MarkDelivered(issued.Id, TimeSpan.FromSeconds(30)); // delivered, never acked

        clock.Advance(TimeSpan.FromSeconds(31));
        controlPlane.SweepAckTimeouts(); // -> back to Pending

        // Agent's next cycle should receive the same command id again, apply it, and ack.
        var received = agent.RunCycle();

        var redelivered = Assert.Single(received);
        Assert.Equal(issued.Id, redelivered.Id);
        Assert.Equal("on", agent.Actual.Settings["featureY"]);
        Assert.Equal(CommandStatus.Acked, commandStore.Get(issued.Id)!.Status);
    }

    [Fact]
    public void Exhausting_max_attempts_marks_the_command_Failed_and_issues_a_fresh_one_next_cycle()
    {
        var deviceId = new DeviceId("dev_1");
        var (agent, controlPlane, commandStore, clock) = BuildHarness(deviceId, maxAttempts: 2);

        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureX"] = "on" }));

        var issued = Assert.Single(controlPlane.ReportStateAndGetNewCommands(deviceId, ActualState.Empty));

        // Attempt 1: delivered, times out, redelivered (Attempts=1 < max=2).
        controlPlane.MarkDelivered(issued.Id, TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(31));
        var firstSweep = Assert.Single(controlPlane.SweepAckTimeouts());
        Assert.Equal(CommandStatus.Pending, firstSweep.Status);

        // Attempt 2: delivered again, times out again, now Attempts=2 == max -> Failed.
        controlPlane.MarkDelivered(issued.Id, TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(31));
        var secondSweep = Assert.Single(controlPlane.SweepAckTimeouts());
        Assert.Equal(CommandStatus.Failed, secondSweep.Status);
        Assert.Equal(CommandStatus.Failed, commandStore.Get(issued.Id)!.Status);

        // The gap is still open (device never got the setting) -- the next agent cycle
        // must issue a brand-new command, not resurrect the failed one.
        var nextCycleCommands = agent.RunCycle();
        var freshCommand = Assert.Single(nextCycleCommands);
        Assert.NotEqual(issued.Id, freshCommand.Id);
        Assert.Equal("set:featureX=on", freshCommand.Action);
        Assert.Equal("on", agent.Actual.Settings["featureX"]);
    }

    [Fact]
    public void Desired_state_changing_mid_flight_supersedes_the_stale_command_and_issues_the_correct_one()
    {
        var deviceId = new DeviceId("dev_1");
        var (_, controlPlane, commandStore, clock) = BuildHarness(deviceId);

        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureX"] = "on" }));

        var issued = Assert.Single(controlPlane.ReportStateAndGetNewCommands(deviceId, ActualState.Empty));
        Assert.Equal("set:featureX=on", issued.Action);

        // Desired state changes before the device ever reports back (command still
        // Pending, per the synchronous flow -- ReportStateAndGetNewCommands issues it
        // but the agent hasn't applied/acked yet in this test).
        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureX"] = "off" }));

        var nextCommands = controlPlane.ReportStateAndGetNewCommands(deviceId, ActualState.Empty);

        Assert.Equal(CommandStatus.Failed, commandStore.Get(issued.Id)!.Status); // superseded

        var replacement = Assert.Single(nextCommands);
        Assert.NotEqual(issued.Id, replacement.Id);
        Assert.Equal("set:featureX=off", replacement.Action);
    }
}
