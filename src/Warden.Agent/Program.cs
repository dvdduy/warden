using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warden.Agent;
using Warden.Core;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AgentServiceOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddWindowsService(options => options.ServiceName = "Warden Agent");

builder.Services.AddSingleton<ISystemCommandRunner, ProcessSystemCommandRunner>();
builder.Services.AddSingleton<FakeBitLockerState>();
builder.Services.AddSingleton<IActualStateProvider>(services =>
{
    var options = services.GetRequiredService<IOptions<AgentServiceOptions>>().Value;
    return options.UseFakeBitLocker
        ? services.GetRequiredService<FakeBitLockerState>()
        : services.GetRequiredService<BitLockerActualStateProvider>();
});
builder.Services.AddSingleton<ICommandExecutor>(services =>
{
    var options = services.GetRequiredService<IOptions<AgentServiceOptions>>().Value;
    return options.UseFakeBitLocker
        ? services.GetRequiredService<FakeBitLockerState>()
        : services.GetRequiredService<BitLockerCommandExecutor>();
});
builder.Services.AddSingleton<BitLockerActualStateProvider>();
builder.Services.AddSingleton<BitLockerCommandExecutor>();
builder.Services.AddSingleton<IControlPlaneClient>(services =>
{
    var options = services.GetRequiredService<IOptions<AgentServiceOptions>>().Value;
    return new RestControlPlaneClient(new HttpClient
    {
        BaseAddress = options.ControlPlaneBaseAddress
    });
});
builder.Services.AddHostedService<ReportingAgentWorker>();
builder.Services.AddHostedService<IpcPipeHostedService>();

var host = builder.Build();

// Fake mode simulates BitLocker in memory -- it never touches real disk encryption. It's
// a config flag, not a build-time switch, so nothing stops it from being left on for a
// real managed device by accident. Since that would silently turn a security-compliance
// agent into a no-op, make it as loud as possible at startup rather than a line in a
// config file nobody re-reads.
var agentOptions = host.Services.GetRequiredService<IOptions<AgentServiceOptions>>().Value;
if (agentOptions.UseFakeBitLocker)
{
    host.Services.GetRequiredService<ILogger<Program>>().LogWarning(
        "*** Agent:UseFakeBitLocker=true -- BitLocker state is SIMULATED, not real. " +
        "This agent will NOT encrypt or report actual disk state. " +
        "This must never be set on a real managed device. ***");
}

await host.RunAsync();
