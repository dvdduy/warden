using Npgsql;
using Warden.Core;
using Warden.ControlPlane;
using Warden.ControlPlane.Api;
using Warden.ControlPlane.Postgres;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IClock, SystemClock>();

// Postgres if a connection string is configured (docker-compose.yml's defaults, or
// ConnectionStrings:Postgres / WARDEN_DB); otherwise in-memory, so `dotnet run` works
// with zero setup for a quick smoke test. Either way, ControlPlane and every endpoint
// below are written against the interfaces alone -- this is the same storage seam
// Session 1 proved out, now serving real HTTP traffic instead of test methods.
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? Environment.GetEnvironmentVariable("WARDEN_DB");

if (connectionString is not null)
{
    var dataSource = NpgsqlDataSource.Create(connectionString);
    PostgresSchema.EnsureCreated(dataSource);
    builder.Services.AddSingleton(dataSource);
    builder.Services.AddSingleton<IDeviceRepository, PostgresDeviceRepository>();
    builder.Services.AddSingleton<ICommandStore, PostgresCommandStore>();
    builder.Services.AddSingleton<IDashboardReadModel, PostgresDashboardReadModel>();
}
else
{
    builder.Services.AddSingleton<IDeviceRepository, InMemoryDeviceRepository>();
    builder.Services.AddSingleton<ICommandStore, InMemoryCommandStore>();
    builder.Services.AddSingleton<IDashboardReadModel, InMemoryDashboardReadModel>();
}

builder.Services.AddSingleton<Warden.ControlPlane.ControlPlane>();

var app = builder.Build();

// Maps the domain's guarded/idempotent exceptions to HTTP status codes at the transport
// boundary -- exactly the kind of concern the Learn section calls out as HTTP-layer, not
// domain-layer. Reconciler and the command state machine never know an HTTP request
// exists.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (KeyNotFoundException ex)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (InvalidCommandTransitionException ex)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

app.MapPost("/devices/enroll", (
    EnrollRequest request,
    Warden.ControlPlane.ControlPlane controlPlane,
    IDashboardReadModel dashboard) =>
{
    var device = controlPlane.RegisterDevice(new DeviceId(request.DeviceId), request.Hostname);
    if (app.Configuration.GetValue<bool>("Warden:SeedBitLockerPolicy"))
    {
        controlPlane.SetDesiredState(device.Id, new DesiredState(new Dictionary<string, string>
        {
            ["bitlocker.enabled"] = "true"
        }));
    }

    dashboard.RecordDevice(device);
    return Results.Ok(DeviceDto.From(device));
});

app.MapPost("/devices/{deviceId}/report-state", (
    string deviceId,
    ReportStateRequest request,
    Warden.ControlPlane.ControlPlane controlPlane,
    IDashboardReadModel dashboard,
    IClock clock) =>
{
    var actual = new ActualState(request.Settings);
    var commands = controlPlane.ReportStateAndGetNewCommands(new DeviceId(deviceId), actual);
    dashboard.RecordActualState(new DeviceId(deviceId), actual, clock.UtcNow);
    return Results.Ok(commands.Select(CommandDto.From));
});

app.MapPost("/commands/{commandId}/delivered", (string commandId, Warden.ControlPlane.ControlPlane controlPlane) =>
{
    var command = controlPlane.MarkDelivered(new CommandId(commandId), TimeSpan.FromSeconds(30));
    return Results.Ok(CommandDto.From(command));
});

app.MapPost("/commands/{commandId}/ack", (string commandId, Warden.ControlPlane.ControlPlane controlPlane) =>
{
    var command = controlPlane.Ack(new CommandId(commandId));
    return Results.Ok(CommandDto.From(command));
});

app.MapGet("/health", (Warden.ControlPlane.ControlPlane controlPlane) => Results.Ok(controlPlane.GetHealthSnapshot()));

app.MapGet("/dashboard/data", (IDashboardReadModel dashboard) => Results.Ok(dashboard.GetRows()));

app.MapGet("/dashboard", (IDashboardReadModel dashboard) =>
{
    var rows = dashboard.GetRows();
    var html = DashboardHtml.Render(rows);
    return Results.Content(html, "text/html; charset=utf-8");
});

app.Run();

// Exposed for WebApplicationFactory<Program> in Warden.Rest.Tests.
public partial class Program;
