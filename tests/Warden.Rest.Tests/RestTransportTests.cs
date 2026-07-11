using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Warden.Agent;
using Warden.ControlPlane.Api;
using Warden.Core;
using Xunit;

namespace Warden.Rest.Tests;

/// <summary>
/// Re-proves the hard behaviors that matter for the agent/control-plane boundary hold
/// when that boundary is a real HTTP+JSON round trip (via WebApplicationFactory's
/// TestServer, driving the actual ASP.NET Core routing/serialization pipeline) instead of
/// an in-process method call. Nothing in Warden.Core or Warden.Agent's Core.Agent changed
/// to make this work -- only the IControlPlaneClient implementation did, which is exactly
/// what the seam was built to prove in v0.1-core.
///
/// Ack-timeout redelivery isn't re-tested here: it's already proven against both the
/// in-memory store (Warden.Core.Tests) and Postgres (Warden.ControlPlane.Tests), and the
/// API's ack deadline is a real 30-second wall-clock window with no test-only override --
/// re-testing it here would mean either waiting 30 real seconds or growing a test-only
/// endpoint, neither of which teaches anything new about the transport itself.
///
/// The host under test always falls back to in-memory storage (Program.cs only wires
/// Postgres when a connection string is configured, and none is set here), so these tests
/// need no external dependency.
/// </summary>
public class RestTransportTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RestTransportTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private RestControlPlaneClient CreateClient() => new(_factory.CreateClient());

    private Warden.ControlPlane.ControlPlane ControlPlane =>
        _factory.Services.GetRequiredService<Warden.ControlPlane.ControlPlane>();

    [Fact]
    public void Full_cycle_over_REST_drives_a_noncompliant_device_to_compliant()
    {
        var deviceId = new DeviceId("rest-dev-1");
        var client = CreateClient();
        var agent = new Core.Agent(deviceId, "LAPTOP-REST-1", client);
        agent.Register();

        // Setting desired state is an admin capability, not part of the agent<->control
        // plane seam (IControlPlaneClient has no such method in v0.1-core either) -- so
        // this reaches into the test host's DI container directly, same as v0.1-core's
        // own tests call ControlPlane.SetDesiredState directly rather than through a
        // client.
        ControlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["BitLocker"] = "on" }));

        var received = agent.RunCycle();
        Assert.Single(received);
        Assert.Equal("on", agent.Actual.Settings["BitLocker"]);

        var secondCycle = agent.RunCycle();
        Assert.Empty(secondCycle); // already compliant -- proves the in-flight/gap invariant survives JSON round-trip
    }

    [Fact]
    public void Redelivering_an_already_Delivered_command_over_REST_is_a_safe_no_op()
    {
        // The realistic "duplicate delivery" case: the control plane redelivers a
        // command whose first delivery is still in flight (ack lost in transit, not the
        // delivery itself) -- Delivered -> Delivered is a legal no-op per
        // CommandStatusTransitions, and that guard must survive the HTTP round trip
        // unchanged: no re-thrown exception, Attempts not double-incremented.
        var deviceId = new DeviceId("rest-dev-2");
        var client = CreateClient();

        client.Register(deviceId, "LAPTOP-REST-2");
        ControlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["Firewall"] = "on" }));

        var issued = Assert.Single(client.ReportState(deviceId, ActualState.Empty));

        var firstDelivery = client.MarkDelivered(issued.Id);
        var secondDelivery = client.MarkDelivered(issued.Id); // duplicate, over the wire

        Assert.Equal(CommandStatus.Delivered, firstDelivery.Status);
        Assert.Equal(firstDelivery.Attempts, secondDelivery.Attempts); // no double-increment
        Assert.Equal(firstDelivery.AckDeadline, secondDelivery.AckDeadline);

        var acked = client.Ack(issued.Id);
        var duplicateAck = client.Ack(issued.Id); // duplicate ack, also over the wire

        Assert.Equal(CommandStatus.Acked, acked.Status);
        Assert.Equal(acked.AckedAt, duplicateAck.AckedAt); // idempotent -- first ack wins
    }

    [Fact]
    public void Offline_device_reconnecting_over_REST_converges_to_current_desired_state()
    {
        var deviceId = new DeviceId("rest-dev-3");
        var client = CreateClient();
        var agent = new Core.Agent(deviceId, "LAPTOP-REST-3", client);
        agent.Register();

        ControlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["Encryption"] = "on" }));

        // Device never picks this up before going offline (no RunCycle call).

        // Policy changes while the device is offline.
        ControlPlane.SetDesiredState(deviceId, new DesiredState(
            new Dictionary<string, string> { ["Encryption"] = "off" }));

        // Device reconnects and runs its first cycle ever, over REST.
        var received = agent.RunCycle();

        var command = Assert.Single(received);
        Assert.Equal("set:Encryption=off", command.Action); // current desired, not the stale "on"
        Assert.Equal("off", agent.Actual.Settings["Encryption"]);
    }

    [Fact]
    public async Task Dashboard_shows_bitlocker_red_then_green_across_remediation_reports()
    {
        var deviceId = new DeviceId("rest-dashboard-bitlocker");
        var http = _factory.CreateClient();
        var client = new RestControlPlaneClient(http);
        var agent = new Core.Agent(
            deviceId,
            "LAPTOP-DASHBOARD",
            client,
            new ActualState(new Dictionary<string, string>
            {
                ["bitlocker.enabled"] = "false"
            }));
        agent.Register();

        ControlPlane.SetDesiredState(deviceId, new DesiredState(new Dictionary<string, string>
        {
            ["bitlocker.enabled"] = "true"
        }));

        agent.RunCycle();

        var afterDrift = await http.GetFromJsonAsync<List<DashboardComplianceRow>>("/dashboard/data");
        Assert.NotNull(afterDrift);
        var red = afterDrift.Single(r => r.DeviceId == deviceId.Value);
        Assert.Equal("Non-compliant", red.Status);

        var html = await http.GetStringAsync("/dashboard");
        Assert.Contains("LAPTOP-DASHBOARD", html);
        Assert.Contains("Non-compliant", html);

        agent.RunCycle();

        var afterRemediation = await http.GetFromJsonAsync<List<DashboardComplianceRow>>("/dashboard/data");
        Assert.NotNull(afterRemediation);
        var green = afterRemediation.Single(r => r.DeviceId == deviceId.Value);
        Assert.Equal("Compliant", green.Status);
    }
}
