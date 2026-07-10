using Warden.Core;

namespace Warden.Demo;

/// <summary>
/// A transport double that never rejects a transition -- stands in for "the network
/// redelivers the exact same command id while it's still in flight" (as opposed to the
/// sweeper's timeout-driven redelivery, which Scenario 2 demonstrates against the real
/// control plane). Isolates the thing Scenario 1 is actually about: the agent's own
/// applied-command-id set is what prevents the double mutation, independent of whatever
/// the transport or store would otherwise allow.
/// </summary>
public sealed class AlwaysAcceptingClient : IControlPlaneClient
{
    private readonly DeviceId _deviceId;

    public int MarkDeliveredCallCount { get; private set; }
    public int AckCallCount { get; private set; }

    public AlwaysAcceptingClient(DeviceId deviceId) => _deviceId = deviceId;

    public Device Register(DeviceId id, string hostname) =>
        new(id, hostname, ActualState.Empty, DateTimeOffset.UtcNow);

    public IReadOnlyList<Command> ReportState(DeviceId id, ActualState actual) =>
        Array.Empty<Command>();

    public Command MarkDelivered(CommandId commandId)
    {
        MarkDeliveredCallCount++;
        return new Command(commandId, _deviceId, "set:BitLocker=on",
            CommandStatus.Delivered, MarkDeliveredCallCount, DateTimeOffset.UtcNow, null, null);
    }

    public Command Ack(CommandId commandId)
    {
        AckCallCount++;
        return new Command(commandId, _deviceId, "set:BitLocker=on",
            CommandStatus.Acked, AckCallCount, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
    }
}
