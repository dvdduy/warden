using Warden.Agent.Interop;

namespace Warden.Agent;

public sealed class WtsSessionEnumerator : ISessionEnumerator
{
    public IReadOnlyList<int> GetActiveSessionIds()
    {
        return Wtsapi32.EnumerateSessions()
            .Where(s => s.State == WtsConnectState.Active)
            .Select(s => s.SessionId)
            .ToList();
    }
}
