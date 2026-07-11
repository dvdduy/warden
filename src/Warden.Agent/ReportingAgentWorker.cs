using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warden.Core;

namespace Warden.Agent;

public sealed class ReportingAgentWorker : BackgroundService
{
    private readonly IControlPlaneClient _client;
    private readonly IActualStateProvider _actualStateProvider;
    private readonly ICommandExecutor _commandExecutor;
    private readonly AgentServiceOptions _options;
    private readonly ILogger<ReportingAgentWorker> _logger;
    private bool _registered;

    public ReportingAgentWorker(
        IControlPlaneClient client,
        IActualStateProvider actualStateProvider,
        ICommandExecutor commandExecutor,
        IOptions<AgentServiceOptions> options,
        ILogger<ReportingAgentWorker> logger)
    {
        _client = client;
        _actualStateProvider = actualStateProvider;
        _commandExecutor = commandExecutor;
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

        foreach (var command in commands)
        {
            try
            {
                _client.MarkDelivered(command.Id);
                _logger.LogInformation(
                    "Command {CommandId} marked delivered; executing {Action}",
                    command.Id,
                    command.Action);

                _commandExecutor.Execute(command);
                _client.Ack(command.Id);

                _logger.LogInformation(
                    "Command {CommandId} executed and acked",
                    command.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Command {CommandId} failed during local execution and was left unacked for retry",
                    command.Id);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunOnce();
            }
            catch (Exception ex)
            {
                // A transient failure here (control plane briefly unreachable, a
                // manage-bde call that errors) must not take the whole Windows Service
                // down -- BackgroundService's default behavior is to stop the host on an
                // unhandled exception, which would turn one bad poll into a permanently
                // dead agent until someone notices and restarts it. Log and try again
                // next interval instead.
                _logger.LogError(ex, "Poll cycle failed; will retry in {PollInterval}", _options.PollInterval);
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
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
