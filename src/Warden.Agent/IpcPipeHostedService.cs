using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warden.Ipc;

namespace Warden.Agent;

/// <summary>
/// Session 1 of v0.3-ipc: an unauthenticated, unrestricted named-pipe server. On purpose --
/// this is the thing Session 2's ACL and peer-verification work hardens next. Accepts one
/// user-agent connection at a time and replies Pong to Ping until the client disconnects,
/// then waits for the next connection.
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneConnectionAsync(stoppingToken).ConfigureAwait(false);
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

    private async Task RunOneConnectionAsync(CancellationToken stoppingToken)
    {
        await using var server = new NamedPipeServerStream(
            PipeNames.Default,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("IPC client connected");

        while (server.IsConnected)
        {
            var message = await PipeMessageProtocol.ReadAsync(server, stoppingToken).ConfigureAwait(false);
            if (message is null)
            {
                break;
            }

            if (message.Type == PipeMessage.Ping.Type)
            {
                await PipeMessageProtocol.WriteAsync(server, PipeMessage.Pong, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("IPC client disconnected");
    }
}
