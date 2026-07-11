using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Warden.Agent;
using Warden.Core;

namespace Warden.Agent.Tests;

public class SessionAgentManagerTests
{
    private static int NextSessionId() => Random.Shared.Next(100_000, 999_999);

    private static (SessionAgentManager Manager, FakeSessionUserAgentLauncher Launcher, FakeSessionEnumerator Enumerator) CreateManager()
    {
        var launcher = new FakeSessionUserAgentLauncher();
        var enumerator = new FakeSessionEnumerator();
        var manager = new SessionAgentManager(launcher, enumerator, NullLogger<SessionAgentManager>.Instance);
        return (manager, launcher, enumerator);
    }

    private static (
        SessionAgentManager Manager,
        FakeSessionUserAgentLauncher Launcher,
        FakeSessionEnumerator Enumerator,
        FakeClock Clock,
        RecordingRestartDelay Delay) CreateWatchedManager(AgentServiceOptions? options = null)
    {
        var launcher = new FakeSessionUserAgentLauncher();
        var enumerator = new FakeSessionEnumerator();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var delay = new RecordingRestartDelay();
        var manager = new SessionAgentManager(
            launcher,
            enumerator,
            clock,
            delay,
            Options.Create(options ?? new AgentServiceOptions
            {
                UserAgentRestartInitialBackoff = TimeSpan.FromSeconds(1),
                UserAgentRestartMaxBackoff = TimeSpan.FromSeconds(8),
                UserAgentCrashLoopWindow = TimeSpan.FromMinutes(1),
                UserAgentMaxCrashesInWindow = 3
            }),
            NullLogger<SessionAgentManager>.Instance);

        return (manager, launcher, enumerator, clock, delay);
    }

    [Fact]
    public async Task Logon_launches_the_user_agent_for_that_session()
    {
        var (manager, launcher, _) = CreateManager();
        var sessionId = NextSessionId();

        await manager.OnSessionLogonAsync(sessionId);

        Assert.Equal([sessionId], launcher.LaunchedSessions);

        await manager.OnSessionLogoffAsync(sessionId);
    }

    [Fact]
    public async Task Duplicate_logon_notifications_for_the_same_session_are_idempotent()
    {
        var (manager, launcher, _) = CreateManager();
        var sessionId = NextSessionId();

        await manager.OnSessionLogonAsync(sessionId);
        await manager.OnSessionLogonAsync(sessionId);
        await manager.OnSessionLogonAsync(sessionId);

        Assert.Equal([sessionId], launcher.LaunchedSessions);

        await manager.OnSessionLogoffAsync(sessionId);
    }

    [Fact]
    public async Task Logoff_tears_down_the_tracked_session()
    {
        var (manager, launcher, _) = CreateManager();
        var sessionId = NextSessionId();

        await manager.OnSessionLogonAsync(sessionId);
        await manager.OnSessionLogoffAsync(sessionId);

        Assert.Equal([sessionId], launcher.DisposedSessions);
    }

    [Fact]
    public async Task Logoff_of_a_session_that_was_never_logged_on_is_a_noop()
    {
        var (manager, launcher, _) = CreateManager();

        await manager.OnSessionLogoffAsync(NextSessionId());

        Assert.Empty(launcher.DisposedSessions);
    }

    [Fact]
    public async Task A_launch_failure_is_logged_and_does_not_leave_the_session_tracked()
    {
        var (manager, launcher, _) = CreateManager();
        var sessionId = NextSessionId();
        launcher.ThrowOnLaunch(sessionId);

        // Must not throw -- a failure to launch one session's user-agent (e.g. the session
        // logged off again before we got to it) can't be allowed to take the whole host down.
        await manager.OnSessionLogonAsync(sessionId);

        Assert.Empty(launcher.LaunchedSessions);

        // Nothing was tracked for the failed attempt, so a later successful launch isn't
        // blocked by stale state from the failure.
        launcher.StopThrowingOnLaunch(sessionId);
        await manager.OnSessionLogonAsync(sessionId);

        Assert.Equal([sessionId], launcher.LaunchedSessions);

        await manager.OnSessionLogoffAsync(sessionId);
    }

    [Fact]
    public async Task Logoff_during_a_slow_launch_tears_down_the_session_instead_of_leaking_it()
    {
        var (manager, launcher, _) = CreateManager();
        var sessionId = NextSessionId();
        launcher.PauseLaunch(sessionId);

        var logonTask = Task.Run(() => manager.OnSessionLogonAsync(sessionId));

        // Wait until Launch() has actually been entered (and is blocked inside the gate) before
        // racing the logoff past it -- mirrors the real launcher's slow, blocking Win32 call
        // chain (WTSQueryUserToken + CreateProcessAsUser) that the logoff can outrun.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!launcher.HasAttemptedLaunch(sessionId) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(launcher.HasAttemptedLaunch(sessionId));

        await manager.OnSessionLogoffAsync(sessionId);

        launcher.ReleaseLaunch(sessionId);
        await logonTask;

        // The launch that raced past the logoff must notice the session is no longer active and
        // tear itself down -- not leak a process, handle, and pipe loop nobody will ever collect.
        await launcher.WaitForDisposedCountAsync(1);
        Assert.Equal([sessionId], launcher.LaunchedSessions);
        Assert.Equal([sessionId], launcher.DisposedSessions);
    }

    [Fact]
    public async Task Starting_with_an_already_active_session_launches_its_user_agent_without_waiting_for_a_notification()
    {
        var (manager, launcher, enumerator) = CreateManager();
        var sessionId = NextSessionId();
        enumerator.ActiveSessionIds.Add(sessionId);

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal([sessionId], launcher.LaunchedSessions);

        await manager.StopAsync(CancellationToken.None);
        Assert.Equal([sessionId], launcher.DisposedSessions);
    }

    [Fact]
    public async Task User_agent_exit_respawns_the_session_agent()
    {
        var (manager, launcher, _, _, delay) = CreateWatchedManager();
        var sessionId = NextSessionId();

        await manager.OnSessionLogonAsync(sessionId);
        launcher.ExitLatest(sessionId);
        await launcher.WaitForLaunchCountAsync(2);

        Assert.Equal([sessionId, sessionId], launcher.LaunchedSessions);
        Assert.Equal([TimeSpan.FromSeconds(1)], delay.Delays);

        await manager.OnSessionLogoffAsync(sessionId);
    }

    [Fact]
    public async Task Repeated_rapid_user_agent_exits_open_the_circuit_breaker()
    {
        var (manager, launcher, _, _, delay) = CreateWatchedManager();
        var sessionId = NextSessionId();

        await manager.OnSessionLogonAsync(sessionId);

        launcher.ExitLatest(sessionId);
        await launcher.WaitForLaunchCountAsync(2);

        launcher.ExitLatest(sessionId);
        await launcher.WaitForLaunchCountAsync(3);

        launcher.ExitLatest(sessionId);
        await launcher.WaitForDisposedCountAsync(3);

        Assert.Equal([sessionId, sessionId, sessionId], launcher.LaunchedSessions);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delay.Delays);

        await manager.OnSessionLogoffAsync(sessionId);
    }

    [Fact]
    public async Task Crash_after_the_window_expires_uses_the_initial_backoff_again()
    {
        var (manager, launcher, _, clock, delay) = CreateWatchedManager();
        var sessionId = NextSessionId();

        await manager.OnSessionLogonAsync(sessionId);
        launcher.ExitLatest(sessionId);
        await launcher.WaitForLaunchCountAsync(2);

        clock.Advance(TimeSpan.FromMinutes(2));

        launcher.ExitLatest(sessionId);
        await launcher.WaitForLaunchCountAsync(3);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], delay.Delays);

        await manager.OnSessionLogoffAsync(sessionId);
    }

    private sealed class FakeClock : IClock
    {
        private DateTimeOffset _now;

        public FakeClock(DateTimeOffset start) => _now = start;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RecordingRestartDelay : IUserAgentRestartDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }
}
