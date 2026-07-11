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
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("BitLocker status can only be queried on Windows.");
        }

        var result = _commandRunner.Run("manage-bde", new[] { "-status", _volume });
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"manage-bde -status {_volume} failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        return result.StandardOutput;
    }
}
