using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

await builder.Build().RunAsync();
