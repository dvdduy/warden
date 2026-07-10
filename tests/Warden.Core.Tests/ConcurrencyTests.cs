using System.Collections.Concurrent;
using Warden.Core;
using Warden.ControlPlane;
using Xunit;

namespace Warden.Core.Tests;

/// <summary>
/// Fleet-scale concurrency: does anything double-apply or get stuck with 100+ agents
/// hammering one control plane at once? This stresses the thread-safety of
/// InMemoryCommandStore/InMemoryDeviceRepository under real contention (Parallel.ForEach,
/// not a single-threaded loop) rather than the sequential logic already covered
/// elsewhere.
/// </summary>
public class ConcurrencyTests
{
    [Fact]
    public void Many_concurrent_agents_converge_with_no_double_apply_and_no_stuck_commands()
    {
        const int deviceCount = 200;
        var clock = new FakeClock();
        var deviceRepo = new InMemoryDeviceRepository();
        var commandStore = new InMemoryCommandStore();
        var controlPlane = new ControlPlane.ControlPlane(deviceRepo, commandStore, clock);
        var client = new InProcessControlPlaneClient(controlPlane);

        var desired = new DesiredState(new Dictionary<string, string>
        {
            ["BitLocker"] = "on",
            ["Firewall"] = "on",
        });

        var deviceIds = Enumerable.Range(0, deviceCount)
            .Select(i => new DeviceId($"dev_{i}"))
            .ToList();

        foreach (var id in deviceIds)
        {
            controlPlane.SetDesiredState(id, desired);
        }

        var completedAgents = new ConcurrentBag<Core.Agent>();
        var exceptions = new ConcurrentBag<Exception>();

        Parallel.ForEach(deviceIds, id =>
        {
            try
            {
                var agent = new Core.Agent(id, $"host-{id.Value}", client);
                agent.Register();

                // Several cycles per agent, same as a real device polling repeatedly --
                // this is what would surface a race between reading in-flight commands
                // and adding a new one for the same gap if the store weren't safe.
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    agent.RunCycle();
                }

                completedAgents.Add(agent);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
        Assert.Equal(deviceCount, completedAgents.Count);

        foreach (var agent in completedAgents)
        {
            Assert.Equal("on", agent.Actual.Settings["BitLocker"]);
            Assert.Equal("on", agent.Actual.Settings["Firewall"]);
            // Exactly one applied command id per gap -- no double-apply.
            Assert.Equal(2, agent.AppliedCommandIds.Count);
        }

        foreach (var id in deviceIds)
        {
            Assert.Empty(controlPlane.GetInFlightCommands(id)); // no stuck commands
        }

        var health = controlPlane.GetHealthSnapshot();
        Assert.Equal(0, health.InFlightCommands);
        Assert.Equal(0, health.FailedCommands);
        Assert.Equal(deviceCount * 2, health.AckedCommands);
    }

    [Fact]
    public void Concurrent_sweeps_and_agent_cycles_never_double_apply_even_with_redeliveries()
    {
        // Layers the ack-timeout sweeper into the concurrency picture: some devices'
        // commands are deliberately left un-acked long enough to time out and get
        // redelivered while other devices are mid-cycle, all against the same store.
        const int deviceCount = 100;
        var clock = new FakeClock();
        var deviceRepo = new InMemoryDeviceRepository();
        var commandStore = new InMemoryCommandStore();
        var controlPlane = new ControlPlane.ControlPlane(deviceRepo, commandStore, clock, maxDeliveryAttempts: 5);
        var client = new InProcessControlPlaneClient(controlPlane, ackDeadline: TimeSpan.FromSeconds(1));

        var desired = new DesiredState(new Dictionary<string, string> { ["featureX"] = "on" });
        var deviceIds = Enumerable.Range(0, deviceCount).Select(i => new DeviceId($"dev_{i}")).ToList();
        foreach (var id in deviceIds)
        {
            controlPlane.SetDesiredState(id, desired);
        }

        var exceptions = new ConcurrentBag<Exception>();
        var agents = new ConcurrentDictionary<DeviceId, Core.Agent>();

        Parallel.ForEach(deviceIds, id =>
        {
            try
            {
                var agent = new Core.Agent(id, $"host-{id.Value}", client);
                agent.Register();
                agent.RunCycle(); // delivers + acks immediately in this harness
                agents[id] = agent;
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Advance time and sweep concurrently with more agent cycles on already-compliant
        // devices -- the sweeper should find nothing overdue (everything already Acked)
        // and every agent cycle should be a no-op, all without throwing.
        clock.Advance(TimeSpan.FromSeconds(2));

        Parallel.Invoke(
            () => { try { controlPlane.SweepAckTimeouts(); } catch (Exception ex) { exceptions.Add(ex); } },
            () => Parallel.ForEach(deviceIds, id =>
            {
                try
                {
                    agents[id].RunCycle();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));

        Assert.Empty(exceptions);
        foreach (var (id, agent) in agents)
        {
            Assert.Equal("on", agent.Actual.Settings["featureX"]);
            Assert.Single(agent.AppliedCommandIds); // still exactly one mutation each
            Assert.Empty(controlPlane.GetInFlightCommands(id));
        }
    }
}
