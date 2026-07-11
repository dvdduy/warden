using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warden.Ipc;

namespace Warden.Agent;

/// <summary>
/// Session 3 of v0.3-ipc: replaces Session 1/2's single well-known pipe with one pipe per
/// logged-on session, launched and torn down as sessions come and go. More than one person can
/// be logged into the same box at once (RDP, fast user switching) -- each gets their own
/// user-agent process and their own ACL'd, peer-verified pipe from <see cref="WardenPipeServer"/>.
///
/// This class only holds the bookkeeping (which session maps to which process and pipe loop);
/// it does not itself listen for session-change notifications. <c>Microsoft.Win32.SystemEvents
/// .SessionSwitch</c> looked like the "modern Microsoft.Extensions.Hosting equivalent" of
/// <c>ServiceBase.OnSessionChange</c>, but its event args don't carry a session id at all --
/// it's built for the single-session interactive-app case (lock/unlock), not a service watching
/// every session on the box. So <see cref="WardenWindowsService"/> overrides
/// <c>ServiceBase.OnSessionChange</c> for real (the one API that actually reports which session
/// changed) and calls into <see cref="OnSessionLogonAsync"/>/<see cref="OnSessionLogoffAsync"/>
/// here.
/// </summary>
public sealed class SessionAgentManager : IHostedService, IComplianceChangeNotifier
{
    private readonly ISessionUserAgentLauncher _launcher;
    private readonly ISessionEnumerator _sessionEnumerator;
    private readonly ILogger<SessionAgentManager> _logger;
    private readonly ConcurrentDictionary<int, SessionEntry> _sessions = new();
    private CancellationTokenSource _hostCts = new();

    public SessionAgentManager(
        ISessionUserAgentLauncher launcher,
        ISessionEnumerator sessionEnumerator,
        ILogger<SessionAgentManager> logger)
    {
        _launcher = launcher;
        _sessionEnumerator = sessionEnumerator;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _hostCts = new CancellationTokenSource();

        // A session-change subscription only sees logons that happen after this point. If the
        // service restarts while someone is already logged in, that logon event already fired
        // and is never coming again -- catch up on whoever's already active.
        foreach (var sessionId in _sessionEnumerator.GetActiveSessionIds())
        {
            await OnSessionLogonAsync(sessionId).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _hostCts.CancelAsync().ConfigureAwait(false);
        await TearDownAllAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Idempotent: a duplicate logon notification for a session already being served is a
    /// no-op, the same guarantee the rest of this project makes for duplicate command delivery.
    /// </summary>
    public async Task OnSessionLogonAsync(int sessionId)
    {
        if (_sessions.ContainsKey(sessionId))
        {
            return;
        }

        ISessionUserAgentHandle handle;
        try
        {
            handle = _launcher.Launch(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch the user-agent for session {SessionId}", sessionId);
            return;
        }

        var loopCts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token);
        var outboundMessages = Channel.CreateUnbounded<PipeMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var server = new WardenPipeServer(PipeNames.ForSession(sessionId), handle.UserSid, sessionId, _logger);
        var loopTask = RunPipeLoopAsync(server, outboundMessages.Reader, loopCts.Token);
        var entry = new SessionEntry(handle, loopCts, loopTask, outboundMessages.Writer);

        if (!_sessions.TryAdd(sessionId, entry))
        {
            // Lost a race with another logon notification for the same session -- drop what we
            // just built instead of leaking a process and a pipe loop nobody will track.
            await TearDownAsync(entry).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Session {SessionId} logged on; launched user-agent PID {ProcessId}",
            sessionId,
            handle.ProcessId);
    }

    /// <summary>Idempotent: logging off a session we never tracked (or already tore down) is a no-op.</summary>
    public async Task OnSessionLogoffAsync(int sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var entry))
        {
            return;
        }

        await TearDownAsync(entry).ConfigureAwait(false);
        _logger.LogInformation("Session {SessionId} logged off; user-agent torn down", sessionId);
    }

    public Task NotifyComplianceChangedAsync(string rule, string status, CancellationToken cancellationToken = default)
    {
        var message = PipeMessage.ComplianceChanged(rule, status);
        foreach (var (sessionId, entry) in _sessions)
        {
            if (!entry.OutboundMessages.TryWrite(message))
            {
                _logger.LogWarning(
                    "Could not enqueue compliance-change IPC message for session {SessionId}",
                    sessionId);
            }
        }

        return Task.CompletedTask;
    }

    private async Task RunPipeLoopAsync(
        WardenPipeServer server,
        ChannelReader<PipeMessage> outboundMessages,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await server.AcceptAndServeOnceAsync(HandleMessageAsync, outboundMessages, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
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

    private static async Task TearDownAsync(SessionEntry entry)
    {
        entry.OutboundMessages.TryComplete();
        entry.LoopCts.Cancel();
        try
        {
            await entry.LoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected from the cancellation above.
        }
        finally
        {
            entry.LoopCts.Dispose();
            entry.Handle.Dispose();
        }
    }

    private async Task TearDownAllAsync()
    {
        foreach (var sessionId in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(sessionId, out var entry))
            {
                await TearDownAsync(entry).ConfigureAwait(false);
            }
        }
    }

    private sealed record SessionEntry(
        ISessionUserAgentHandle Handle,
        CancellationTokenSource LoopCts,
        Task LoopTask,
        ChannelWriter<PipeMessage> OutboundMessages);
}
