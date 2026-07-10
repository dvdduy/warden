using Warden.Core;
using Xunit;

namespace Warden.Core.Tests;

public class InMemoryDeviceRepositoryTests
{
    [Fact]
    public void Register_creates_a_new_device_with_empty_actual_state()
    {
        var repo = new InMemoryDeviceRepository();
        var clock = new FakeClock();
        var id = new DeviceId("dev_1");

        var device = repo.Register(id, "LAPTOP-01", clock);

        Assert.Equal(id, device.Id);
        Assert.Equal("LAPTOP-01", device.Hostname);
        Assert.Equal(ActualState.Empty, device.Actual);
        Assert.Equal(clock.UtcNow, device.LastSeen);
    }

    [Fact]
    public void Registering_twice_updates_LastSeen_but_preserves_existing_actual_state()
    {
        var repo = new InMemoryDeviceRepository();
        var clock = new FakeClock();
        var id = new DeviceId("dev_1");

        repo.Register(id, "LAPTOP-01", clock);
        repo.ReportActualState(id, new ActualState(new Dictionary<string, string> { ["featureX"] = "on" }), clock);

        clock.Advance(TimeSpan.FromMinutes(1));
        var reRegistered = repo.Register(id, "LAPTOP-01", clock);

        Assert.Equal(clock.UtcNow, reRegistered.LastSeen);
        Assert.Equal("on", reRegistered.Actual.Settings["featureX"]); // not reset to Empty
    }

    [Fact]
    public void GetDesiredState_defaults_to_Empty_when_never_assigned()
    {
        var repo = new InMemoryDeviceRepository();
        var id = new DeviceId("dev_1");

        Assert.Equal(DesiredState.Empty, repo.GetDesiredState(id));
    }

    [Fact]
    public void SetDesiredState_then_GetDesiredState_round_trips()
    {
        var repo = new InMemoryDeviceRepository();
        var id = new DeviceId("dev_1");
        var desired = new DesiredState(new Dictionary<string, string> { ["featureX"] = "on" });

        repo.SetDesiredState(id, desired);

        Assert.Equal(desired, repo.GetDesiredState(id));
    }

    [Fact]
    public void ReportActualState_on_unregistered_device_throws()
    {
        var repo = new InMemoryDeviceRepository();
        var clock = new FakeClock();
        var id = new DeviceId("dev_1");

        Assert.Throws<KeyNotFoundException>(
            () => repo.ReportActualState(id, ActualState.Empty, clock));
    }

    [Fact]
    public void Get_on_unknown_device_returns_null()
    {
        var repo = new InMemoryDeviceRepository();

        Assert.Null(repo.Get(new DeviceId("nonexistent")));
    }
}