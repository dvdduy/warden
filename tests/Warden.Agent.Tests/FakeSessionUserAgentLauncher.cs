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
    private int _nextProcessId = 1000;

    public List<int> LaunchedSessions { get; } = [];

    public List<int> DisposedSessions { get; } = [];

    public void ThrowOnLaunch(int sessionId) => _sessionsThatThrow.Add(sessionId);

    public void StopThrowingOnLaunch(int sessionId) => _sessionsThatThrow.Remove(sessionId);

    public ISessionUserAgentHandle Launch(int sessionId)
    {
        if (_sessionsThatThrow.Contains(sessionId))
        {
            throw new InvalidOperationException($"Simulated launch failure for session {sessionId}");
        }

        LaunchedSessions.Add(sessionId);
        return new FakeHandle(this, sessionId, _nextProcessId++);
    }

    private sealed class FakeHandle : ISessionUserAgentHandle
    {
        private readonly FakeSessionUserAgentLauncher _owner;
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

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.DisposedSessions.Add(SessionId);
        }
    }
}
