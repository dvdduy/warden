using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warden.Agent;
using Warden.Core;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AgentServiceOptions>(builder.Configuration.GetSection("Agent"));

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

builder.Services.AddSingleton<ISessionUserAgentLauncher, CreateProcessAsUserLauncher>();
builder.Services.AddSingleton<ISessionEnumerator, WtsSessionEnumerator>();
builder.Services.AddSingleton<Warden.Core.IClock, Warden.Core.SystemClock>();
builder.Services.AddSingleton<IUserAgentRestartDelay, SystemUserAgentRestartDelay>();
// Registered both as itself (WardenWindowsService.OnSessionChange resolves it directly to
// forward SERVICE_CONTROL_SESSIONCHANGE) and as the IHostedService that starts/stops it with
// everything else -- same singleton instance either way.
builder.Services.AddSingleton<SessionAgentManager>();
builder.Services.AddSingleton<IComplianceChangeNotifier>(services =>
    services.GetRequiredService<SessionAgentManager>());
builder.Services.AddHostedService(services => services.GetRequiredService<SessionAgentManager>());

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

// AddWindowsService() can't be used alongside session-change handling (see
// WardenWindowsService's doc comment), so the SCM/console branch is manual: a real installed
// service gets SERVICE_CONTROL_SESSIONCHANGE via WardenWindowsService; running interactively
// (dev, `dotnet run`) just runs the host directly -- session-change notifications only ever
// arrive when actually launched by the Service Control Manager.
if (WindowsServiceHelpers.IsWindowsService())
{
    ServiceBase.Run(new WardenWindowsService(host));
}
else
{
    await host.RunAsync();
}
