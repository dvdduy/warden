using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warden.Core;

namespace Warden.Agent;

public sealed class ReportingAgentWorker : BackgroundService
{
    private readonly IControlPlaneClient _client;
    private readonly IActualStateProvider _actualStateProvider;
    private readonly AgentServiceOptions _options;
    private readonly ILogger<ReportingAgentWorker> _logger;
    private bool _registered;

    public ReportingAgentWorker(
        IControlPlaneClient client,
        IActualStateProvider actualStateProvider,
        IOptions<AgentServiceOptions> options,
        ILogger<ReportingAgentWorker> logger)
    {
        _client = client;
        _actualStateProvider = actualStateProvider;
        _options = options.Value;
        _logger = logger;
    }

    public void RunOnce()
    {
        EnsureRegistered();

        var actual = _actualStateProvider.GetActualState();
        var commands = _client.ReportState(new DeviceId(_options.DeviceId), actual);

        _logger.LogInformation(
            "Reported actual state for {DeviceId}: {ActualState}",
            _options.DeviceId,
            string.Join(", ", actual.Settings.Select(kv => $"{kv.Key}={kv.Value}")));

        if (commands.Count > 0)
        {
            _logger.LogWarning(
                "Read-only BitLocker session received {Count} command(s): {CommandIds}. Remediation lands in MVP session 4.",
                commands.Count,
                string.Join(",", commands.Select(c => c.Id.Value)));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            RunOnce();
            await Task.Delay(_options.PollInterval, stoppingToken);
        }
    }

    private void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        _client.Register(new DeviceId(_options.DeviceId), _options.Hostname);
        _registered = true;

        _logger.LogInformation(
            "Registered {DeviceId} ({Hostname}) with the control plane",
            _options.DeviceId,
            _options.Hostname);
    }
}
