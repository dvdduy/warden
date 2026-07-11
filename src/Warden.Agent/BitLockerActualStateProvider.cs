using Microsoft.Extensions.Options;
using Warden.Core;

namespace Warden.Agent;

public sealed class BitLockerActualStateProvider : IActualStateProvider
{
    private readonly ISystemCommandRunner _commandRunner;
    private readonly string _volume;

    public BitLockerActualStateProvider(
        ISystemCommandRunner commandRunner,
        IOptions<AgentServiceOptions> options)
    {
        _commandRunner = commandRunner;
        _volume = options.Value.BitLockerVolume;
    }

    public ActualState GetActualState()
    {
        var output = RunManageBdeStatus();
        var enabled = BitLockerStatusParser.IsProtectionEnabled(output);

        return new ActualState(new Dictionary<string, string>
        {
            [BitLockerPolicy.EnabledKey] = enabled ? "true" : "false"
        });
    }

    private string RunManageBdeStatus()
    {
        // No OperatingSystem.IsWindows() guard here -- that would make this class
        // untestable with a fake ISystemCommandRunner on any non-Windows dev/CI machine,
        // for a check that's redundant anyway: on a platform without manage-bde, starting
        // the real process (ProcessSystemCommandRunner) already fails with a clear error.
        var result = _commandRunner.Run("manage-bde", new[] { "-status", _volume });
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"manage-bde -status {_volume} failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        return result.StandardOutput;
    }
}
