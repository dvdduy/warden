using Microsoft.Extensions.Options;
using Warden.Core;

namespace Warden.Agent;

public sealed class BitLockerCommandExecutor : ICommandExecutor
{
    private static readonly HashSet<string> EnableActions = new(StringComparer.OrdinalIgnoreCase)
    {
        $"set:{BitLockerPolicy.EnabledKey}=true",
        "enable-bitlocker"
    };

    private readonly ISystemCommandRunner _commandRunner;
    private readonly string _volume;

    public BitLockerCommandExecutor(
        ISystemCommandRunner commandRunner,
        IOptions<AgentServiceOptions> options)
    {
        _commandRunner = commandRunner;
        _volume = options.Value.BitLockerVolume;
    }

    public void Execute(Command command)
    {
        if (!EnableActions.Contains(command.Action))
        {
            throw new InvalidOperationException($"Unsupported command action for this agent: '{command.Action}'");
        }

        // No OperatingSystem.IsWindows() guard here -- see BitLockerActualStateProvider
        // for why: it's redundant with the real process-start failure on unsupported
        // platforms, and removing it is what makes this testable with a fake
        // ISystemCommandRunner regardless of what OS the tests run on.
        var result = _commandRunner.Run("manage-bde", new[] { "-on", _volume });
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"manage-bde -on {_volume} failed with exit code {result.ExitCode}: {result.StandardError}");
        }
    }
}
