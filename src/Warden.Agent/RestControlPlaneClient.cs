using System.Net.Http.Json;
using Warden.Core;

namespace Warden.Agent;

/// <summary>
/// v0.2-mvp's REST implementation of the IControlPlaneClient seam: the same four calls
/// InProcessControlPlaneClient makes as plain method calls, now serialized over HTTP.
/// Warden.Agent's Core.Agent never changes -- it depends only on IControlPlaneClient, so
/// swapping InProcessControlPlaneClient for this one is a one-line change at the
/// composition root, exactly what the seam was built for in v0.1-core.
///
/// IControlPlaneClient's methods are synchronous (a v0.1-core constraint pinned in
/// Warden.Core, which this course doesn't touch), so every call here blocks on the async
/// HTTP request via GetAwaiter().GetResult() rather than exposing Task-returning methods.
/// That's a real, deliberate trade-off, not an oversight -- see DESIGN.md's open
/// questions.
/// </summary>
public sealed class RestControlPlaneClient : IControlPlaneClient
{
    private readonly HttpClient _http;

    public RestControlPlaneClient(HttpClient http) => _http = http;

    public Device Register(DeviceId id, string hostname)
    {
        var response = _http.PostAsJsonAsync("/devices/enroll", new EnrollRequestDto(id.Value, hostname))
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var dto = response.Content.ReadFromJsonAsync<DeviceDto>().GetAwaiter().GetResult()!;
        return dto.ToDevice();
    }

    public IReadOnlyList<Command> ReportState(DeviceId id, ActualState actual)
    {
        var response = _http
            .PostAsJsonAsync($"/devices/{id.Value}/report-state",
                new ReportStateRequestDto(new Dictionary<string, string>(actual.Settings)))
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var dtos = response.Content.ReadFromJsonAsync<List<CommandDto>>().GetAwaiter().GetResult()!;
        return dtos.Select(d => d.ToCommand()).ToList();
    }

    public Command MarkDelivered(CommandId commandId)
    {
        var response = _http.PostAsync($"/commands/{commandId.Value}/delivered", content: null)
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var dto = response.Content.ReadFromJsonAsync<CommandDto>().GetAwaiter().GetResult()!;
        return dto.ToCommand();
    }

    public Command Ack(CommandId commandId)
    {
        var response = _http.PostAsync($"/commands/{commandId.Value}/ack", content: null)
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var dto = response.Content.ReadFromJsonAsync<CommandDto>().GetAwaiter().GetResult()!;
        return dto.ToCommand();
    }
}

// Mirrors Warden.ControlPlane.Api's DTOs exactly (see that project's Dtos.cs for why
// these aren't shared via a project reference).
internal sealed record EnrollRequestDto(string DeviceId, string Hostname);

internal sealed record ReportStateRequestDto(Dictionary<string, string> Settings);

internal sealed record DeviceDto(string Id, string Hostname, Dictionary<string, string> Actual, DateTimeOffset LastSeen)
{
    public Device ToDevice() => new(new DeviceId(Id), Hostname, new ActualState(Actual), LastSeen);
}

internal sealed record CommandDto(
    string Id,
    string DeviceId,
    string Action,
    string Status,
    int Attempts,
    DateTimeOffset IssuedAt,
    DateTimeOffset? AckDeadline,
    DateTimeOffset? AckedAt)
{
    public Command ToCommand() => new(
        new CommandId(Id), new Core.DeviceId(DeviceId), Action, Enum.Parse<CommandStatus>(Status),
        Attempts, IssuedAt, AckDeadline, AckedAt);
}
