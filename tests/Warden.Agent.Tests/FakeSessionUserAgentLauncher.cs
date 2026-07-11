using System.Collections.Concurrent;
using System.Security.Principal;
using Warden.Agent;

namespace Warden.Agent.Tests;

/// <summary>
/// Session 3's real launcher needs SeTcbPrivilege (only LocalSystem has it), so it can't be
/// exercised in an ordinary test run -- this fake stands in, matching the FakeClock/
/// FakeBitLockerState pattern already used elsewhere in this project for I/O the tests can't
/// perform for real.
/// </summary>
internal sealed class FakeSessionUserAgentLauncher : ISessionUserAgentLauncher
{
    private readonly HashSet<int> _sessionsThatThrow = [];
    private readonly List<FakeHandle> _handles = [];
    private readonly List<LaunchWaiter> _launchWaiters = [];
    private readonly List<DisposeWaiter> _disposeWaiters = [];
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _launchGates = new();
    private readonly ConcurrentDictionary<int, byte> _launchAttempts = new();
    private int _nextProcessId = 1000;

    public List<int> LaunchedSessions { get; } = [];

    public List<int> DisposedSessions { get; } = [];

    public void ThrowOnLaunch(int sessionId) => _sessionsThatThrow.Add(sessionId);

    public void StopThrowingOnLaunch(int sessionId) => _sessionsThatThrow.Remove(sessionId);

    public void ExitLatest(int sessionId) =>
        _handles.Last(h => h.SessionId == sessionId && !h.Exited).Exit();

    /// <summary>Makes the next Launch(sessionId) block inside the call until ReleaseLaunch runs -- simulates the real launcher's slow, blocking Win32 call chain so tests can race a logoff against it.</summary>
    public void PauseLaunch(int sessionId) =>
        _launchGates[sessionId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseLaunch(int sessionId)
    {
        if (_launchGates.TryGetValue(sessionId, out var gate))
        {
            gate.TrySetResult();
        }
    }

    public bool HasAttemptedLaunch(int sessionId) => _launchAttempts.ContainsKey(sessionId);

    public Task WaitForLaunchCountAsync(int count)
    {
        if (LaunchedSessions.Count >= count)
        {
            return Task.CompletedTask;
        }

        var waiter = new LaunchWaiter(count, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _launchWaiters.Add(waiter);
        return waiter.Completion.Task;
    }

    public Task WaitForDisposedCountAsync(int count)
    {
        if (DisposedSessions.Count >= count)
        {
            return Task.CompletedTask;
        }

        var waiter = new DisposeWaiter(count, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _disposeWaiters.Add(waiter);
        return waiter.Completion.Task;
    }

    public ISessionUserAgentHandle Launch(int sessionId)
    {
        if (_sessionsThatThrow.Contains(sessionId))
        {
            throw new InvalidOperationException($"Simulated launch failure for session {sessionId}");
        }

        _launchAttempts[sessionId] = 0;

        if (_launchGates.TryGetValue(sessionId, out var gate))
        {
            gate.Task.GetAwaiter().GetResult();
        }

        LaunchedSessions.Add(sessionId);
        var handle = new FakeHandle(this, sessionId, _nextProcessId++);
        _handles.Add(handle);
        foreach (var waiter in _launchWaiters.Where(w => LaunchedSessions.Count >= w.Count).ToList())
        {
            waiter.Completion.SetResult();
            _launchWaiters.Remove(waiter);
        }

        return handle;
    }

    private sealed record LaunchWaiter(int Count, TaskCompletionSource Completion);

    private sealed record DisposeWaiter(int Count, TaskCompletionSource Completion);

    private sealed class FakeHandle : ISessionUserAgentHandle
    {
        private readonly FakeSessionUserAgentLauncher _owner;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public FakeHandle(FakeSessionUserAgentLauncher owner, int sessionId, int processId)
        {
            _owner = owner;
            SessionId = sessionId;
            ProcessId = processId;
            using var identity = WindowsIdentity.GetCurrent();
            UserSid = identity.User ?? throw new InvalidOperationException("Could not resolve the current user's SID.");
        }

        public int SessionId { get; }

        public int ProcessId { get; }

        public SecurityIdentifier UserSid { get; }

        public bool Exited { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _exited.Task.WaitAsync(cancellationToken);

        public void Exit()
        {
            Exited = true;
            _exited.TrySetResult();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.DisposedSessions.Add(SessionId);
            foreach (var waiter in _owner._disposeWaiters.Where(w => _owner.DisposedSessions.Count >= w.Count).ToList())
            {
                waiter.Completion.SetResult();
                _owner._disposeWaiters.Remove(waiter);
            }
        }
    }
}
