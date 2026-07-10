namespace Warden.Core;

/// <summary>
/// Abstraction over "what time is it," so that anything with timeout or retry logic
/// (the ack-timeout sweeper, Session 5) depends on an injected clock rather than
/// DateTime.UtcNow directly. Production code uses SystemClock; tests use a FakeClock
/// (in Warden.Core.Tests) that advances programmatically — no real sleeping, ever,
/// in a test suite that needs to prove "redeliver after 30s" behavior.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock. Used everywhere outside tests.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}