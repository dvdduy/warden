using Warden.Agent;

namespace Warden.Agent.Tests;

internal sealed class FakeSessionEnumerator : ISessionEnumerator
{
    public List<int> ActiveSessionIds { get; } = [];

    public IReadOnlyList<int> GetActiveSessionIds() => ActiveSessionIds;
}
