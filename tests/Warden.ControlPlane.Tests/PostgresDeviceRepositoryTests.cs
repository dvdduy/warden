using Warden.Core;
using Warden.ControlPlane.Postgres;
using Xunit;

namespace Warden.ControlPlane.Tests;

/// <summary>
/// Proves PostgresDeviceRepository matches InMemoryDeviceRepository's contract (see
/// Warden.Core.Tests.InMemoryDeviceRepositoryTests) against a real database.
/// </summary>
[Collection("Postgres")]
public class PostgresDeviceRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public PostgresDeviceRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    /// <summary>
    /// Postgres `timestamptz` has microsecond resolution, coarser than a DateTimeOffset
    /// tick (100ns) -- truncate at construction so `clock.UtcNow` already equals whatever
    /// comes back from a round trip through the database, instead of failing assertions
    /// on a sub-microsecond remainder that was never going to survive persistence.
    /// </summary>
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; }

        public FixedClock(DateTimeOffset now) => UtcNow = new DateTimeOffset(now.Ticks - (now.Ticks % 10), now.Offset);
    }

    [Fact]
    public void Register_creates_a_new_device_with_empty_actual_state()
    {
        var repo = new PostgresDeviceRepository(_fixture.DataSource);
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var id = new DeviceId("dev_1");

        var device = repo.Register(id, "LAPTOP-01", clock);

        Assert.Equal(id, device.Id);
        Assert.Equal("LAPTOP-01", device.Hostname);
        Assert.Empty(device.Actual.Settings);
        Assert.Equal(clock.UtcNow, device.LastSeen);
    }

    [Fact]
    public void Repeated_registration_only_bumps_LastSeen_never_resets_actual_state()
    {
        var repo = new PostgresDeviceRepository(_fixture.DataSource);
        var id = new DeviceId("dev_1");

        repo.Register(id, "LAPTOP-01", new FixedClock(DateTimeOffset.UtcNow));
        repo.ReportActualState(id, new ActualState(new Dictionary<string, string> { ["featureX"] = "on" }),
            new FixedClock(DateTimeOffset.UtcNow));

        var laterClock = new FixedClock(DateTimeOffset.UtcNow.AddMinutes(5));
        var reregistered = repo.Register(id, "LAPTOP-01", laterClock);

        Assert.Equal("on", reregistered.Actual.Settings["featureX"]); // not reset
        Assert.Equal(laterClock.UtcNow, reregistered.LastSeen);
    }

    [Fact]
    public void GetDesiredState_for_never_assigned_device_returns_Empty()
    {
        var repo = new PostgresDeviceRepository(_fixture.DataSource);

        var desired = repo.GetDesiredState(new DeviceId("dev_never_seen"));

        Assert.Empty(desired.Settings);
    }

    [Fact]
    public void SetDesiredState_works_even_if_the_device_never_registered()
    {
        // Matches InMemoryDeviceRepository: desired state is stored independently of
        // device registration -- desired_states is its own table, not a devices column.
        var repo = new PostgresDeviceRepository(_fixture.DataSource);
        var id = new DeviceId("dev_never_registered");

        repo.SetDesiredState(id, new DesiredState(new Dictionary<string, string> { ["featureX"] = "on" }));
        var desired = repo.GetDesiredState(id);

        Assert.Equal("on", desired.Settings["featureX"]);
    }

    [Fact]
    public void ReportActualState_on_unregistered_device_throws()
    {
        var repo = new PostgresDeviceRepository(_fixture.DataSource);
        var clock = new FixedClock(DateTimeOffset.UtcNow);

        Assert.Throws<KeyNotFoundException>(() =>
            repo.ReportActualState(new DeviceId("dev_ghost"), ActualState.Empty, clock));
    }

    [Fact]
    public void Get_on_unknown_device_returns_null()
    {
        var repo = new PostgresDeviceRepository(_fixture.DataSource);

        Assert.Null(repo.Get(new DeviceId("dev_unknown")));
    }
}
