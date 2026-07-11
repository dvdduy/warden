using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warden.Ipc;

namespace Warden.Agent;

/// <summary>
/// Session 2 of v0.3-ipc: the pipe from Session 1, now ACL'd to LocalSystem + one user SID and
/// peer-verified against that user's session. The allowed SID and expected session are
/// resolved from this process's own identity for now -- Session 3 replaces that with real
/// per-logged-on-session tracking via WTSQueryUserToken, once there's more than one session to
/// track. Accepts one user-agent connection at a time; a rejected connection just waits for the
/// next one instead of tearing the host down.
/// </summary>
public sealed class IpcPipeHostedService : BackgroundService
{
    private readonly ILogger<IpcPipeHostedService> _logger;

    public IpcPipeHostedService(ILogger<IpcPipeHostedService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var allowedUserSid = identity.User
            ?? throw new InvalidOperationException("Could not resolve the current user's SID for the IPC pipe ACL.");
        var expectedSessionId = Process.GetCurrentProcess().SessionId;

        var server = new WardenPipeServer(PipeNames.Default, allowedUserSid, expectedSessionId, _logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await server.AcceptAndServeOnceAsync(HandleMessageAsync, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IPC pipe connection failed; waiting for the next client");
            }
        }
    }

    private static Task<PipeMessage?> HandleMessageAsync(PipeMessage message, CancellationToken cancellationToken)
    {
        return Task.FromResult(message.Type == PipeMessage.Ping.Type ? PipeMessage.Pong : null);
    }
}
