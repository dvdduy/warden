using Warden.Core;

namespace Warden.Core.Tests;

/// <summary>
/// Controllable clock for tests. Starts at a fixed instant and only moves when
/// explicitly advanced — so a test for "redeliver after 30s ack timeout" (Session 5)
/// takes milliseconds to run and never flakes on CI load, instead of calling
/// Task.Delay(30_000) and hoping.
/// </summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; }

    public FakeClock(DateTimeOffset? start = null)
    {
        UtcNow = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public void Advance(TimeSpan by) => UtcNow += by;
}