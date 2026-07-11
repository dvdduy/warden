using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Warden.Ipc;

namespace Warden.Ipc.Tests;

public class WardenPipeServerTests
{
    [Fact]
    public async Task Ping_round_trips_when_the_client_session_matches_the_expected_session()
    {
        var pipeName = $"WardenIpcTest-{Guid.NewGuid():N}";
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var currentUserSid = currentIdentity.User!;
        var actualSessionId = Process.GetCurrentProcess().SessionId;

        var server = new WardenPipeServer(pipeName, currentUserSid, actualSessionId, NullLogger.Instance);
        var serverTask = server.AcceptAndServeOnceAsync(
            (message, _) => Task.FromResult(message.Type == PipeMessage.Ping.Type ? PipeMessage.Pong : null),
            CancellationToken.None);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);

        await PipeMessageProtocol.WriteAsync(client, PipeMessage.Ping);
        var response = await PipeMessageProtocol.ReadAsync(client);

        client.Dispose();
        var accepted = await serverTask;

        Assert.Equal(PipeMessage.Pong.Type, response?.Type);
        Assert.True(accepted);
    }

    [Fact]
    public async Task ComplianceChanged_message_can_be_pushed_to_the_connected_user_agent()
    {
        var pipeName = $"WardenIpcTest-{Guid.NewGuid():N}";
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var currentUserSid = currentIdentity.User!;
        var actualSessionId = Process.GetCurrentProcess().SessionId;
        var outboundMessages = Channel.CreateUnbounded<PipeMessage>();

        var server = new WardenPipeServer(pipeName, currentUserSid, actualSessionId, NullLogger.Instance);
        var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.AcceptAndServeOnceAsync(
            (message, _) => Task.FromResult(message.Type == PipeMessage.Ping.Type ? PipeMessage.Pong : null),
            outboundMessages.Reader,
            serverCts.Token);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);

        await PipeMessageProtocol.WriteAsync(client, PipeMessage.Ping);
        Assert.Equal(PipeMessage.Pong.Type, (await PipeMessageProtocol.ReadAsync(client))?.Type);

        Assert.True(outboundMessages.Writer.TryWrite(PipeMessage.ComplianceChanged("bitlocker.enabled", "Compliant")));

        var pushed = await PipeMessageProtocol.ReadAsync(client, serverCts.Token);
        var payload = pushed?.TryGetComplianceChangedPayload();

        Assert.Equal("ComplianceChanged", pushed?.Type);
        Assert.Equal("bitlocker.enabled", payload?.Rule);
        Assert.Equal("Compliant", payload?.Status);

        await serverCts.CancelAsync();
        await serverTask;
    }

    /// <summary>
    /// A fault on the outbound side (e.g. the pipe breaking mid-delivery of a queued
    /// ComplianceChanged message) must not vanish as an unobserved task exception just because
    /// the inbound side happened to finish first -- it has to be logged.
    /// </summary>
    [Fact]
    public async Task A_fault_on_the_losing_side_of_the_connection_is_logged_not_swallowed()
    {
        var pipeName = $"WardenIpcTest-{Guid.NewGuid():N}";
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var currentUserSid = currentIdentity.User!;
        var actualSessionId = Process.GetCurrentProcess().SessionId;
        var outboundMessages = Channel.CreateUnbounded<PipeMessage>();
        var recordingLogger = new RecordingLogger();
        var simulatedFault = new InvalidOperationException("simulated broken pipe mid-delivery");

        var server = new WardenPipeServer(pipeName, currentUserSid, actualSessionId, recordingLogger);
        var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.AcceptAndServeOnceAsync(
            (message, _) => Task.FromResult(message.Type == PipeMessage.Ping.Type ? PipeMessage.Pong : null),
            outboundMessages.Reader,
            serverCts.Token);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);

        // Fault the outbound channel instead of writing a real message, and disconnect the
        // client -- this races the outbound fault against the inbound side's disconnect
        // detection, exactly the "loser of Task.WhenAny" scenario.
        outboundMessages.Writer.Complete(simulatedFault);
        client.Dispose();

        var accepted = await serverTask;

        Assert.True(accepted);
        Assert.Contains(recordingLogger.Errors, e => ReferenceEquals(e, simulatedFault));
    }

    /// <summary>
    /// The ACL would let this connection through -- it's the right user's SID -- but the
    /// session it's connecting from doesn't match the session this pipe was created for. That's
    /// exactly the case ACLs alone can't catch: peer verification has to reject it anyway.
    /// </summary>
    [Fact]
    public async Task Connection_is_rejected_when_the_client_session_does_not_match_the_expected_session()
    {
        var pipeName = $"WardenIpcTest-{Guid.NewGuid():N}";
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var currentUserSid = currentIdentity.User!;
        var wrongExpectedSessionId = Process.GetCurrentProcess().SessionId + 999_999;

        var server = new WardenPipeServer(pipeName, currentUserSid, wrongExpectedSessionId, NullLogger.Instance);
        var serverTask = server.AcceptAndServeOnceAsync(
            (_, _) => Task.FromResult<PipeMessage?>(PipeMessage.Pong),
            CancellationToken.None);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);

        var accepted = await serverTask;
        Assert.False(accepted);

        // The server closed the pipe the moment it rejected the peer -- the client's Ping never
        // gets a reply, even though the OS-level connection (ACL check) succeeded. Depending on
        // exactly when the OS notices the broken pipe, the client sees either an IOException or
        // a clean EOF (null) -- both mean "no reply came back."
        PipeMessage? response = null;
        try
        {
            await PipeMessageProtocol.WriteAsync(client, PipeMessage.Ping);
            response = await PipeMessageProtocol.ReadAsync(client);
        }
        catch (IOException)
        {
            // Expected: the server tore down the pipe before replying.
        }

        Assert.Null(response);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<Exception> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null)
            {
                Errors.Add(exception);
            }
        }
    }
}
