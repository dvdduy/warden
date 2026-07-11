using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Warden.Ipc;

/// <summary>
/// The hardened replacement for Session 1's bare <c>NamedPipeServerStream</c> loop: the ACL
/// restricts who can even open the pipe, and <see cref="PipePeerVerifier"/> double-checks the
/// connection actually came from the session this pipe was created for -- defense in depth
/// against an ACL that's technically correct but the connection still isn't who it claims.
/// </summary>
public sealed class WardenPipeServer
{
    private readonly string _pipeName;
    private readonly SecurityIdentifier _allowedUserSid;
    private readonly int _expectedSessionId;
    private readonly ILogger _logger;

    public WardenPipeServer(
        string pipeName,
        SecurityIdentifier allowedUserSid,
        int expectedSessionId,
        ILogger? logger = null)
    {
        _pipeName = pipeName;
        _allowedUserSid = allowedUserSid;
        _expectedSessionId = expectedSessionId;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Accepts one connection and, if it passes peer verification, services messages on it
    /// until the client disconnects. Returns <c>false</c> without invoking
    /// <paramref name="handleMessageAsync"/> if the connecting peer's session doesn't match
    /// <see cref="_expectedSessionId"/>; returns <c>true</c> for a normal session (verified
    /// connect through client disconnect).
    /// </summary>
    public async Task<bool> AcceptAndServeOnceAsync(
        Func<PipeMessage, CancellationToken, Task<PipeMessage?>> handleMessageAsync,
        CancellationToken cancellationToken)
    {
        var security = WardenPipeSecurity.Create(_allowedUserSid);

        await using var server = NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);

        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        var clientSessionId = PipePeerVerifier.GetClientSessionId(server);
        if (clientSessionId != _expectedSessionId)
        {
            _logger.LogWarning(
                "SECURITY: rejected IPC connection from session {ClientSessionId}; this pipe belongs to session {ExpectedSessionId}",
                clientSessionId,
                _expectedSessionId);
            return false;
        }

        _logger.LogInformation("IPC client connected from session {SessionId}", clientSessionId);

        while (server.IsConnected)
        {
            var message = await PipeMessageProtocol.ReadAsync(server, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                break;
            }

            var response = await handleMessageAsync(message, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                await PipeMessageProtocol.WriteAsync(server, response, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("IPC client disconnected");
        return true;
    }
}
