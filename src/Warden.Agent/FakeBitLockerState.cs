using Microsoft.Extensions.Options;
using Warden.Core;

namespace Warden.Agent;

public sealed class FakeBitLockerState : IActualStateProvider, ICommandExecutor
{
    private readonly object _gate = new();
    private bool _enabled;

    public FakeBitLockerState(IOptions<AgentServiceOptions> options) =>
        _enabled = options.Value.FakeBitLockerEnabled;

    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    public ActualState GetActualState()
    {
        lock (_gate)
        {
            return new ActualState(new Dictionary<string, string>
            {
                [BitLockerPolicy.EnabledKey] = _enabled ? "true" : "false"
            });
        }
    }

    public void Execute(Command command)
    {
        if (!string.Equals(command.Action, $"set:{BitLockerPolicy.EnabledKey}=true", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(command.Action, "enable-bitlocker", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported command action for fake BitLocker: '{command.Action}'");
        }

        lock (_gate)
        {
            _enabled = true;
        }
    }
}
