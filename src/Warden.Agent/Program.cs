using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Warden.Agent;
using Warden.Core;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AgentServiceOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddWindowsService(options => options.ServiceName = "Warden Agent");

builder.Services.AddSingleton<ISystemCommandRunner, ProcessSystemCommandRunner>();
builder.Services.AddSingleton<IActualStateProvider, BitLockerActualStateProvider>();
builder.Services.AddSingleton<ICommandExecutor, BitLockerCommandExecutor>();
builder.Services.AddSingleton<IControlPlaneClient>(services =>
{
    var options = services.GetRequiredService<IOptions<AgentServiceOptions>>().Value;
    return new RestControlPlaneClient(new HttpClient
    {
        BaseAddress = options.ControlPlaneBaseAddress
    });
});
builder.Services.AddHostedService<ReportingAgentWorker>();

await builder.Build().RunAsync();
