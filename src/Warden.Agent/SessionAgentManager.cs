using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warden.Core;
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
    private readonly IClock _clock;
    private readonly IUserAgentRestartDelay _restartDelay;
    private readonly AgentServiceOptions _options;
    private readonly ILogger<SessionAgentManager> _logger;
    private readonly ConcurrentDictionary<int, SessionEntry> _sessions = new();
    private readonly ConcurrentDictionary<int, byte> _activeSessions = new();
    private readonly ConcurrentDictionary<int, byte> _circuitOpenSessions = new();
    private CancellationTokenSource _hostCts = new();

    public SessionAgentManager(
        ISessionUserAgentLauncher launcher,
        ISessionEnumerator sessionEnumerator,
        ILogger<SessionAgentManager> logger)
        : this(
            launcher,
            sessionEnumerator,
            new SystemClock(),
            new SystemUserAgentRestartDelay(),
            Options.Create(new AgentServiceOptions()),
            logger)
    {
    }

    public SessionAgentManager(
        ISessionUserAgentLauncher launcher,
        ISessionEnumerator sessionEnumerator,
        IClock clock,
        IUserAgentRestartDelay restartDelay,
        IOptions<AgentServiceOptions> options,
        ILogger<SessionAgentManager> logger)
    {
        _launcher = launcher;
        _sessionEnumerator = sessionEnumerator;
        _clock = clock;
        _restartDelay = restartDelay;
        _options = options.Value;
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
        _activeSessions[sessionId] = 0;

        if (_circuitOpenSessions.ContainsKey(sessionId))
        {
            _logger.LogWarning(
                "Ignoring duplicate logon for session {SessionId}; user-agent watchdog circuit is open",
                sessionId);
            return;
        }

        if (_sessions.ContainsKey(sessionId))
        {
            return;
        }

        await StartSessionAsync(sessionId, new RestartState(_options.UserAgentRestartInitialBackoff))
            .ConfigureAwait(false);
    }

    private async Task StartSessionAsync(int sessionId, RestartState restartState)
    {
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
        var entry = new SessionEntry(handle, loopCts, loopTask, outboundMessages.Writer, restartState);

        if (!_sessions.TryAdd(sessionId, entry))
        {
            // Lost a race with another logon notification for the same session -- drop what we
            // just built instead of leaking a process and a pipe loop nobody will track.
            await TearDownAsync(entry, awaitMonitor: false).ConfigureAwait(false);
            return;
        }

        entry.MonitorTask = MonitorUserAgentExitAsync(sessionId, entry);

        if (!_activeSessions.ContainsKey(sessionId))
        {
            // The session logged off while _launcher.Launch (a slow, blocking Win32 call) was
            // still in flight -- OnSessionLogoffAsync ran, found nothing in _sessions yet, and
            // returned having done no teardown. Catch up on the teardown it missed instead of
            // leaking this entry forever; only remove it if it's still exactly the entry we just
            // added, so we don't tear down a session that's since logged back on and replaced it.
            if (_sessions.TryRemove(KeyValuePair.Create(sessionId, entry)))
            {
                _logger.LogInformation(
                    "Session {SessionId} logged off while its user-agent was still launching; tearing down PID {ProcessId}",
                    sessionId,
                    handle.ProcessId);
                await TearDownAsync(entry, awaitMonitor: true).ConfigureAwait(false);
            }

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
        _activeSessions.TryRemove(sessionId, out _);
        _circuitOpenSessions.TryRemove(sessionId, out _);

        if (!_sessions.TryRemove(sessionId, out var entry))
        {
            return;
        }

        await TearDownAsync(entry, awaitMonitor: true).ConfigureAwait(false);
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

    private async Task MonitorUserAgentExitAsync(int sessionId, SessionEntry entry)
    {
        try
        {
            await entry.Handle.WaitForExitAsync(entry.LoopCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_hostCts.IsCancellationRequested || !_activeSessions.ContainsKey(sessionId))
        {
            return;
        }

        if (!_sessions.TryRemove(KeyValuePair.Create(sessionId, entry)))
        {
            return;
        }

        _logger.LogWarning(
            "User-agent PID {ProcessId} exited unexpectedly for session {SessionId}",
            entry.Handle.ProcessId,
            sessionId);

        await TearDownAsync(entry, awaitMonitor: false).ConfigureAwait(false);
        await RestartAfterCrashAsync(sessionId, entry.RestartState).ConfigureAwait(false);
    }

    private async Task RestartAfterCrashAsync(int sessionId, RestartState restartState)
    {
        var now = _clock.UtcNow;
        restartState.Prune(now, _options.UserAgentCrashLoopWindow);
        if (restartState.CrashCount == 0)
        {
            restartState.ResetBackoff(_options.UserAgentRestartInitialBackoff);
        }

        restartState.RecordCrash(now);
        if (restartState.CrashCount >= _options.UserAgentMaxCrashesInWindow)
        {
            _circuitOpenSessions[sessionId] = 0;
            _logger.LogError(
                "User-agent for session {SessionId} crashed {CrashCount} times inside {CrashWindow}; circuit opened",
                sessionId,
                restartState.CrashCount,
                _options.UserAgentCrashLoopWindow);
            return;
        }

        var delay = restartState.NextDelay;
        restartState.IncreaseBackoff(_options.UserAgentRestartMaxBackoff);

        _logger.LogWarning(
            "Restarting user-agent for session {SessionId} after {Delay}",
            sessionId,
            delay);

        await _restartDelay.DelayAsync(delay, _hostCts.Token).ConfigureAwait(false);

        if (_hostCts.IsCancellationRequested ||
            !_activeSessions.ContainsKey(sessionId) ||
            _circuitOpenSessions.ContainsKey(sessionId))
        {
            return;
        }

        await StartSessionAsync(sessionId, restartState).ConfigureAwait(false);
    }

    private static async Task TearDownAsync(SessionEntry entry, bool awaitMonitor)
    {
        entry.OutboundMessages.TryComplete();
        entry.LoopCts.Cancel();
        try
        {
            await entry.LoopTask.ConfigureAwait(false);
            if (awaitMonitor && entry.MonitorTask is not null)
            {
                await entry.MonitorTask.ConfigureAwait(false);
            }
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
            _activeSessions.TryRemove(sessionId, out _);
            _circuitOpenSessions.TryRemove(sessionId, out _);
            if (_sessions.TryRemove(sessionId, out var entry))
            {
                await TearDownAsync(entry, awaitMonitor: true).ConfigureAwait(false);
            }
        }
    }

    private sealed class SessionEntry
    {
        public SessionEntry(
            ISessionUserAgentHandle handle,
            CancellationTokenSource loopCts,
            Task loopTask,
            ChannelWriter<PipeMessage> outboundMessages,
            RestartState restartState)
        {
            Handle = handle;
            LoopCts = loopCts;
            LoopTask = loopTask;
            OutboundMessages = outboundMessages;
            RestartState = restartState;
        }

        public ISessionUserAgentHandle Handle { get; }

        public CancellationTokenSource LoopCts { get; }

        public Task LoopTask { get; }

        public ChannelWriter<PipeMessage> OutboundMessages { get; }

        public RestartState RestartState { get; }

        public Task? MonitorTask { get; set; }
    }

    private sealed class RestartState
    {
        private readonly Queue<DateTimeOffset> _crashes = new();

        public RestartState(TimeSpan initialDelay)
        {
            NextDelay = initialDelay;
        }

        public TimeSpan NextDelay { get; private set; }

        public int CrashCount => _crashes.Count;

        public void RecordCrash(DateTimeOffset when) => _crashes.Enqueue(when);

        public void Prune(DateTimeOffset now, TimeSpan window)
        {
            while (_crashes.TryPeek(out var crash) && now - crash > window)
            {
                _crashes.Dequeue();
            }
        }

        public void ResetBackoff(TimeSpan initialDelay) => NextDelay = initialDelay;

        public void IncreaseBackoff(TimeSpan maxDelay)
        {
            var doubledTicks = NextDelay.Ticks * 2;
            NextDelay = TimeSpan.FromTicks(Math.Min(doubledTicks, maxDelay.Ticks));
        }
    }
}
