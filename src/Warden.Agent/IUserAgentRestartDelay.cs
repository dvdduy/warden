namespace Warden.Agent;

public interface IUserAgentRestartDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemUserAgentRestartDelay : IUserAgentRestartDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
