using Warden.Core;
using Warden.ControlPlane;
using Xunit;

namespace Warden.Core.Tests;

/// <summary>
/// Hard behavior #2: offline -> reconnect. No special queue or backlog on the agent --
/// a device that skips cycles just reconciles against *current* desired state when it
/// comes back, without replaying anything stale.
/// </summary>
public class OfflineReconcileTests
{
    private static (Core.Agent agent, ControlPlane.ControlPlane controlPlane, FakeClock clock) BuildHarness(DeviceId deviceId)
    {
        var clock = new FakeClock();
        var deviceRepo = new InMemoryDeviceRepository();
        var commandStore = new InMemoryCommandStore();
        var controlPlane = new ControlPlane.ControlPlane(deviceRepo, commandStore, clock);
        var client = new InProcessControlPlaneClient(controlPlane);
        var agent = new Core.Agent(deviceId, "test-device", client);
        agent.Register();

        return (agent, controlPlane, clock);
    }

    [Fact]
    public void Agent_that_skips_cycles_converges_to_current_desired_state_on_return()
    {
        var deviceId = new DeviceId("dev_1");
        var (agent, controlPlane, _) = BuildHarness(deviceId);

        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureX"] = "on" }));

        // Device goes offline before ever running a cycle -- simulate by simply not
        // calling RunCycle for a while (no polling, no queued work).

        // ... and reconnects.
        var received = agent.RunCycle();

        var command = Assert.Single(received);
        Assert.Equal("set:featureX=on", command.Action);
        Assert.Equal("on", agent.Actual.Settings["featureX"]);
    }

    [Fact]
    public void Desired_state_that_changed_while_offline_is_what_the_device_reconciles_to_not_the_old_value()
    {
        var deviceId = new DeviceId("dev_1");
        var (agent, controlPlane, _) = BuildHarness(deviceId);

        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureX"] = "on" }));

        // Device never picks this up before going offline.

        // While offline, policy changes.
        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureX"] = "off" }));

        // Device reconnects and runs its first cycle ever.
        var received = agent.RunCycle();

        var command = Assert.Single(received);
        Assert.Equal("set:featureX=off", command.Action); // current desired, not the stale "on"
        Assert.Equal("off", agent.Actual.Settings["featureX"]);
    }

    [Fact]
    public void Offline_device_does_not_replay_a_command_that_failed_out_while_it_was_gone()
    {
        // A command was issued, delivered, timed out repeatedly, and failed while the
        // device was offline (e.g. driven by the sweeper against stale AckDeadlines).
        // On reconnect, the device must get a *fresh* command for the still-open gap,
        // never the failed one resurrected.
        var deviceId = new DeviceId("dev_1");
        var clock = new FakeClock();
        var deviceRepo = new InMemoryDeviceRepository();
        var commandStore = new InMemoryCommandStore();
        var controlPlane = new ControlPlane.ControlPlane(deviceRepo, commandStore, clock, maxDeliveryAttempts: 1);
        var client = new InProcessControlPlaneClient(controlPlane);
        var agent = new Core.Agent(deviceId, "test-device", client);
        agent.Register();

        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureX"] = "on" }));

        var issued = Assert.Single(controlPlane.ReportStateAndGetNewCommands(deviceId, ActualState.Empty));
        controlPlane.MarkDelivered(issued.Id, TimeSpan.FromSeconds(30));

        clock.Advance(TimeSpan.FromSeconds(31));
        var swept = Assert.Single(controlPlane.SweepAckTimeouts()); // maxAttempts=1 -> straight to Failed
        Assert.Equal(CommandStatus.Failed, swept.Status);

        // Device "reconnects" now and reports its still-empty actual state.
        var received = agent.RunCycle();

        var freshCommand = Assert.Single(received);
        Assert.NotEqual(issued.Id, freshCommand.Id);
        Assert.Equal("set:featureX=on", freshCommand.Action);
        Assert.Equal("on", agent.Actual.Settings["featureX"]);
    }

    [Fact]
    public void Reconnecting_with_no_desired_state_changes_and_already_compliant_issues_nothing()
    {
        var deviceId = new DeviceId("dev_1");
        var (agent, controlPlane, _) = BuildHarness(deviceId);

        // No desired state ever set -- device is trivially compliant even after "being
        // offline" for a long stretch (simulated by simply never having run a cycle).
        var received = agent.RunCycle();

        Assert.Empty(received);
        Assert.Empty(controlPlane.GetInFlightCommands(deviceId));
    }
}
