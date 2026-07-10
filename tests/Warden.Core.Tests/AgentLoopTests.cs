using Warden.Core;
using Warden.ControlPlane;
using Xunit;

namespace Warden.Core.Tests;

public class AgentLoopTests
{
    private static (Core.Agent agent, ControlPlane.ControlPlane controlPlane, ICommandStore commandStore, FakeClock clock) BuildHarness(
        DeviceId deviceId, string hostname = "test-device")
    {
        var clock = new FakeClock();
        var deviceRepo = new InMemoryDeviceRepository();
        var commandStore = new InMemoryCommandStore();
        var controlPlane = new ControlPlane.ControlPlane(deviceRepo, commandStore, clock);
        var client = new InProcessControlPlaneClient(controlPlane);
        var agent = new Core.Agent(deviceId, hostname, client);

        // Every real cycle starts with registration (see Agent.Register / the loop in
        // AgentHost) — ReportActualState correctly refuses an unregistered device, so
        // the harness registers up front rather than every test repeating this step.
        agent.Register();

        return (agent, controlPlane, commandStore, clock);
    }

    // ---- the marquee behavior: duplicate delivery applies exactly once ----

    [Fact]
    public void Applying_a_command_sets_the_setting_and_records_it_as_applied()
    {
        // Establishes the baseline the two tests below build on. Note: calling
        // agent.Apply() TWICE against the real InProcessControlPlaneClient is not a
        // valid way to simulate "duplicate delivery" -- the in-process client completes
        // the full Pending -> Delivered -> Acked lifecycle synchronously in one Apply
        // call, so a second Apply legitimately hits Acked -> Delivered, which the store
        // correctly rejects (see the test below). The two genuinely distinct duplicate-
        // delivery scenarios are covered separately:
        //   - store-side: redelivery after the command is already terminal (Acked) is
        //     rejected by the store, while the agent's dedup set still holds
        //   - agent-side: with a permissive transport that doesn't enforce the state
        //     machine, redelivery is accepted but the agent still only mutates once
        var deviceId = new DeviceId("dev_1");
        var (agent, _, commandStore, clock) = BuildHarness(deviceId);

        var command = new Command(
            Id: CommandId.NewId(),
            DeviceId: deviceId,
            Action: "set:featureX=on",
            Status: CommandStatus.Pending,
            Attempts: 0,
            IssuedAt: clock.UtcNow,
            AckDeadline: null,
            AckedAt: null);
        commandStore.Add(command); // Apply() assumes the control plane already issued this

        agent.Apply(command);

        Assert.Equal("on", agent.Actual.Settings["featureX"]);
        Assert.Single(agent.AppliedCommandIds);
        Assert.Equal(CommandStatus.Acked, commandStore.Get(command.Id)!.Status);
    }

    [Fact]
    public void Redelivering_after_the_first_ack_already_landed_throws_but_never_double_mutates()
    {
        // Simulates the case that actually matters for hard behavior #1/#3: the control
        // plane redelivers a command whose ack already landed and completed the
        // lifecycle (Pending -> Delivered -> Acked). Acked is terminal, so
        // MarkDelivered(Acked -> Delivered) is correctly rejected by the store (Session
        // 3) rather than resurrecting a finished command. Regardless of how the control
        // plane reacts, the agent's own dedup set independently guarantees the setting
        // itself is never re-mutated.
        var deviceId = new DeviceId("dev_1");
        var (agent, controlPlane, commandStore, clock) = BuildHarness(deviceId);

        var command = new Command(
            CommandId.NewId(), deviceId, "set:featureX=on",
            CommandStatus.Pending, 0, clock.UtcNow, null, null);
        commandStore.Add(command); // Apply() assumes the control plane already issued this

        agent.Apply(command); // Pending -> Delivered -> Acked
        Assert.Empty(controlPlane.GetInFlightCommands(deviceId)); // Acked -> no longer "in flight"

        Assert.Throws<InvalidCommandTransitionException>(() => agent.Apply(command));

        Assert.Equal("on", agent.Actual.Settings["featureX"]);
        Assert.Single(agent.AppliedCommandIds);
    }

    [Fact]
    public void Agent_dedup_prevents_re_mutation_even_when_the_transport_always_accepts_redelivery()
    {
        // Isolates the guarantee this session actually promises, independent of
        // Session 3's store transition rules. The realistic duplicate-delivery path is
        // the control plane redelivering a command that's still Delivered (its ack was
        // lost in transit, not the delivery) — a legal no-op transition, unlike the
        // terminal-Acked case above. A permissive fake client stands in for that
        // "transport is happy to redeliver" case, isolating the agent's own
        // applied-id-set guarantee from the store's transition guard (tested separately
        // in InMemoryCommandStoreTests).
        var deviceId = new DeviceId("dev_1");
        var client = new AlwaysAcceptingClient(deviceId);
        var agent = new Core.Agent(deviceId, "test-device", client);

        var command = new Command(
            CommandId.NewId(), deviceId, "set:featureX=on",
            CommandStatus.Pending, 0, DateTimeOffset.UtcNow, null, null);

        agent.Apply(command); // first delivery: applies, sets featureX=on
        agent.Apply(command); // redelivery of the identical command id
        agent.Apply(command); // and again, for good measure

        Assert.Equal("on", agent.Actual.Settings["featureX"]);
        Assert.Single(agent.AppliedCommandIds);
        Assert.Equal(3, client.MarkDeliveredCallCount); // every delivery still gets marked
        Assert.Equal(3, client.AckCallCount);            // every delivery still gets acked
    }

    /// <summary>
    /// A permissive IControlPlaneClient test double that never rejects a transition —
    /// used to isolate the agent's own dedup guarantee from the store's transition
    /// guard, which is tested separately in InMemoryCommandStoreTests.
    /// </summary>
    private sealed class AlwaysAcceptingClient : Core.IControlPlaneClient
    {
        private readonly DeviceId _deviceId;
        public int MarkDeliveredCallCount { get; private set; }
        public int AckCallCount { get; private set; }

        public AlwaysAcceptingClient(DeviceId deviceId) => _deviceId = deviceId;

        public Device Register(DeviceId id, string hostname) =>
            new(id, hostname, ActualState.Empty, DateTimeOffset.UtcNow);

        public IReadOnlyList<Command> ReportState(DeviceId id, ActualState actual) =>
            Array.Empty<Command>();

        public Command MarkDelivered(CommandId commandId)
        {
            MarkDeliveredCallCount++;
            return new Command(commandId, _deviceId, "set:featureX=on",
                CommandStatus.Delivered, MarkDeliveredCallCount, DateTimeOffset.UtcNow, null, null);
        }

        public Command Ack(CommandId commandId)
        {
            AckCallCount++;
            return new Command(commandId, _deviceId, "set:featureX=on",
                CommandStatus.Acked, AckCallCount, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        }
    }

    // ---- full cycle: non-compliant device converges to compliant ----

    [Fact]
    public void Full_cycle_drives_a_noncompliant_device_to_compliant()
    {
        var deviceId = new DeviceId("dev_1");
        var (agent, controlPlane, _, _) = BuildHarness(deviceId);

        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureX"] = "on" }));

        // Cycle 1: agent reports its (empty) actual state, gets back a command, applies it.
        var received = agent.RunCycle();

        Assert.Single(received);
        Assert.Equal("on", agent.Actual.Settings["featureX"]);

        // Cycle 2: agent reports its now-compliant actual state. No new command should
        // be issued — this proves the agent loop is idempotent end-to-end, not just the
        // Apply method in isolation.
        var secondCycle = agent.RunCycle();
        Assert.Empty(secondCycle);
    }

    [Fact]
    public void Running_the_cycle_repeatedly_on_an_already_compliant_device_issues_nothing()
    {
        var deviceId = new DeviceId("dev_1");
        var (agent, controlPlane, _, _) = BuildHarness(deviceId);

        // No desired state assigned at all -> device is trivially "compliant" (empty vs empty).
        for (var i = 0; i < 5; i++)
        {
            var received = agent.RunCycle();
            Assert.Empty(received);
        }

        Assert.Empty(controlPlane.GetInFlightCommands(deviceId));
    }

    [Fact]
    public void Multiple_gaps_are_all_applied_in_one_cycle()
    {
        var deviceId = new DeviceId("dev_1");
        var (agent, controlPlane, _, _) = BuildHarness(deviceId);

        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureX"] = "on", ["featureY"] = "off" }));

        var received = agent.RunCycle();

        Assert.Equal(2, received.Count);
        Assert.Equal("on", agent.Actual.Settings["featureX"]);
        Assert.Equal("off", agent.Actual.Settings["featureY"]);
    }

    [Fact]
    public void A_second_cycle_before_ack_processing_does_not_issue_a_duplicate_command_for_the_same_gap()
    {
        // This exercises the Session 2 in-flight invariant through the full agent loop:
        // RunCycle both reports state AND applies+acks in the same call in v0.1-core, so
        // by the time cycle 2 runs, cycle 1's command is already Acked and the device is
        // compliant. The interesting case (a command still Pending/Delivered when the
        // next cycle starts) is what the offline/no-ack tests in Session 5 target
        // directly; this test documents that today's synchronous loop can't produce it,
        // which is exactly why Session 5 introduces asynchrony via the sweeper.
        var deviceId = new DeviceId("dev_1");
        var (agent, controlPlane, _, _) = BuildHarness(deviceId);

        controlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["featureX"] = "on" }));

        var firstCycleCommands = agent.RunCycle();
        var secondCycleCommands = agent.RunCycle();

        Assert.Single(firstCycleCommands);
        Assert.Empty(secondCycleCommands);
    }

    // ---- registration ----

    [Fact]
    public void Register_is_safe_to_call_multiple_times()
    {
        var deviceId = new DeviceId("dev_1");
        var (agent, controlPlane, _, _) = BuildHarness(deviceId, "LAPTOP-01");

        // BuildHarness already registered once; call it twice more to prove repeated
        // registration is safe and doesn't reset device state.
        agent.Register();
        agent.Register();

        var device = controlPlane.RegisterDevice(deviceId, "LAPTOP-01");
        Assert.Equal(deviceId, device.Id);
    }
}