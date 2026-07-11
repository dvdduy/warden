using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Warden.Agent;

/// <summary>
/// The Generic Host's <c>AddWindowsService()</c> integration wraps its own internal
/// <c>ServiceBase</c> and doesn't expose <c>OnSessionChange</c>, so multi-session tracking needs
/// a hand-rolled <c>ServiceBase</c> instead. This class is only the SCM bridge: start/stop the
/// already-built <see cref="IHost"/>, and forward <c>SERVICE_CONTROL_SESSIONCHANGE</c> into
/// <see cref="SessionAgentManager"/>. All the actual reconciliation, IPC, and session bookkeeping
/// logic is unchanged and still lives in the host's registered services.
/// </summary>
public sealed class WardenWindowsService : ServiceBase
{
    private readonly IHost _host;

    public WardenWindowsService(IHost host)
    {
        _host = host;
        ServiceName = "Warden Agent";
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnStart(string[] args)
    {
        _host.StartAsync().GetAwaiter().GetResult();
    }

    protected override void OnStop()
    {
        _host.StopAsync().GetAwaiter().GetResult();
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        base.OnSessionChange(changeDescription);

        var manager = _host.Services.GetRequiredService<SessionAgentManager>();
        var logger = _host.Services.GetRequiredService<ILogger<WardenWindowsService>>();

        switch (changeDescription.Reason)
        {
            case SessionChangeReason.SessionLogon:
                _ = manager.OnSessionLogonAsync(changeDescription.SessionId);
                break;
            case SessionChangeReason.SessionLogoff:
                _ = manager.OnSessionLogoffAsync(changeDescription.SessionId);
                break;
            default:
                logger.LogDebug(
                    "Ignoring session change {Reason} for session {SessionId}",
                    changeDescription.Reason,
                    changeDescription.SessionId);
                break;
        }
    }
}
