using Warden.Core;

namespace Warden.Demo;

/// <summary>
/// A manually-advanced IClock for the scripted demo scenarios (duplicate delivery,
/// ack-timeout redelivery, offline reconcile) -- lets the demo show "30 seconds pass"
/// instantly instead of actually sleeping, same reasoning as FakeClock in the test
/// project. Not shared with Warden.Core.Tests because Warden.Demo doesn't reference the
/// test project (see the dependency rule in CLAUDE.md).
/// </summary>
public sealed class DemoClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;

    public void Advance(TimeSpan by) => UtcNow += by;
}
