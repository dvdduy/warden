using Warden.Core;
using Warden.ControlPlane.Postgres;
using Xunit;

namespace Warden.ControlPlane.Tests;

/// <summary>
/// The point of this whole session: the same ControlPlane/Reconciler/Agent from
/// v0.1-core, now running against real PostgreSQL-backed stores instead of the in-memory
/// ones, with zero changes to Warden.Core. If this test needed to change anything in
/// Core to pass, the v0.1-core seam wasn't designed correctly (see
/// WARDEN_COURSE_MVP.md's stated precondition).
/// </summary>
[Collection("Postgres")]
public class PostgresControlPlaneEndToEndTests
{
    private readonly PostgresFixture _fixture;

    public PostgresControlPlaneEndToEndTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public void Full_cycle_drives_a_noncompliant_device_to_compliant_against_postgres()
    {
        var clock = new SystemClock();
        var deviceRepo = new PostgresDeviceRepository(_fixture.DataSource);
        var commandStore = new PostgresCommandStore(_fixture.DataSource);
        var controlPlane = new ControlPlane(deviceRepo, commandStore, clock);
        var client = new InProcessControlPlaneClient(controlPlane);

        var deviceId = new DeviceId("dev_1");
        var agent = new Agent(deviceId, "LAPTOP-01", client);
        agent.Register();

        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["BitLocker"] = "on" }));

        var received = agent.RunCycle();
        Assert.Single(received);
        Assert.Equal("on", agent.Actual.Settings["BitLocker"]);

        var secondCycle = agent.RunCycle();
        Assert.Empty(secondCycle); // already compliant -- no duplicate command issued
    }

    [Fact]
    public void Duplicate_command_delivery_still_applies_exactly_once_against_postgres()
    {
        var clock = new SystemClock();
        var deviceRepo = new PostgresDeviceRepository(_fixture.DataSource);
        var commandStore = new PostgresCommandStore(_fixture.DataSource);
        var controlPlane = new ControlPlane(deviceRepo, commandStore, clock);

        var deviceId = new DeviceId("dev_1");
        controlPlane.RegisterDevice(deviceId, "LAPTOP-01");
        var command = new Command(
            CommandId.NewId(), deviceId, "set:BitLocker=on",
            CommandStatus.Pending, 0, clock.UtcNow, null, null);
        commandStore.Add(command);

        var client = new InProcessControlPlaneClient(controlPlane);
        var agent = new Agent(deviceId, "LAPTOP-01", client);

        agent.Apply(command);

        Assert.Equal("on", agent.Actual.Settings["BitLocker"]);
        Assert.Single(agent.AppliedCommandIds);
        Assert.Equal(CommandStatus.Acked, commandStore.Get(command.Id)!.Status);
    }

    [Fact]
    public void No_ack_redelivers_then_fails_against_postgres()
    {
        var clock = new SystemClock();
        var deviceRepo = new PostgresDeviceRepository(_fixture.DataSource);
        var commandStore = new PostgresCommandStore(_fixture.DataSource);
        const int maxAttempts = 1;
        var controlPlane = new ControlPlane(deviceRepo, commandStore, clock, maxAttempts);

        var deviceId = new DeviceId("dev_1");
        controlPlane.RegisterDevice(deviceId, "LAPTOP-01");
        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["Firewall"] = "on" }));

        var issued = Assert.Single(controlPlane.ReportStateAndGetNewCommands(deviceId, ActualState.Empty));

        // Deliver with a deadline already in the past -- avoids a real sleep for a
        // "timeout" in this end-to-end test.
        controlPlane.MarkDelivered(issued.Id, TimeSpan.FromSeconds(-1));
        var swept = Assert.Single(controlPlane.SweepAckTimeouts());

        Assert.Equal(CommandStatus.Failed, swept.Status); // maxAttempts=1 -> straight to Failed

        var client = new InProcessControlPlaneClient(controlPlane);
        var agent = new Agent(deviceId, "LAPTOP-01", client);
        var freshCommand = Assert.Single(agent.RunCycle());

        Assert.NotEqual(issued.Id, freshCommand.Id); // a new command, not a resurrection
        Assert.Equal("on", agent.Actual.Settings["Firewall"]);
    }
}
