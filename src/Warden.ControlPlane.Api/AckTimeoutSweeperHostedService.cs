namespace Warden.ControlPlane.Api;

/// <summary>
/// Runs the ack-timeout sweep on a timer for the lifetime of the API host. Without this,
/// ControlPlane.SweepAckTimeouts() is never called by anything in this process -- a
/// command that's Delivered but never acked would sit in that status forever instead of
/// redelivering (attempts < max) or landing in Failed (attempts exhausted). This is what
/// makes hard behavior #4 ("no-ack -> bounded retry -> Failed") actually run in the
/// deployed v0.2-mvp stack, not just in tests and Warden.Demo.
/// </summary>
public sealed class AckTimeoutSweeperHostedService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    private readonly Warden.ControlPlane.ControlPlane _controlPlane;
    private readonly ILogger<AckTimeoutSweeperHostedService> _logger;

    public AckTimeoutSweeperHostedService(
        Warden.ControlPlane.ControlPlane controlPlane,
        ILogger<AckTimeoutSweeperHostedService> logger)
    {
        _controlPlane = controlPlane;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var swept = _controlPlane.SweepAckTimeouts();
                if (swept.Count > 0)
                {
                    _logger.LogInformation(
                        "Ack-timeout sweep processed {Count} overdue command(s)", swept.Count);
                }
            }
            catch (Exception ex)
            {
                // A failed sweep (e.g. a transient DB blip) must not kill the loop --
                // that would silently disable hard behavior #4 for the rest of the
                // process's lifetime. Log and try again next interval.
                _logger.LogError(ex, "Ack-timeout sweep failed; will retry in {Interval}", SweepInterval);
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }
}
