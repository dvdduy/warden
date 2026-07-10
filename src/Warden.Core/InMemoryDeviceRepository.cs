namespace Warden.Core;

/// <summary>
/// Thread-safe, in-memory <see cref="IDeviceRepository"/>. Same rationale as
/// InMemoryCommandStore (Session 3): a single lock around plain dictionaries, since
/// every operation here is small and the read-modify-write shape of Register /
/// ReportActualState needs synchronization regardless of the underlying collection type.
/// Replaced by a PostgreSQL-backed implementation in v0.2-mvp behind this same interface.
/// </summary>
public sealed class InMemoryDeviceRepository : IDeviceRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<DeviceId, Device> _devices = new();
    private readonly Dictionary<DeviceId, DesiredState> _desiredStates = new();

    public Device Register(DeviceId id, string hostname, IClock clock)
    {
        lock (_gate)
        {
            if (_devices.TryGetValue(id, out var existing))
            {
                var reseen = existing with { LastSeen = clock.UtcNow };
                _devices[id] = reseen;
                return reseen;
            }

            var device = new Device(id, hostname, ActualState.Empty, clock.UtcNow);
            _devices[id] = device;
            return device;
        }
    }

    public DesiredState GetDesiredState(DeviceId id)
    {
        lock (_gate)
        {
            return _desiredStates.TryGetValue(id, out var desired) ? desired : DesiredState.Empty;
        }
    }

    public void SetDesiredState(DeviceId id, DesiredState desired)
    {
        lock (_gate)
        {
            _desiredStates[id] = desired;
        }
    }

    public Device ReportActualState(DeviceId id, ActualState actual, IClock clock)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var existing))
            {
                throw new KeyNotFoundException(
                    $"Device {id} has not registered. Call Register before reporting state.");
            }

            var updated = existing with { Actual = actual, LastSeen = clock.UtcNow };
            _devices[id] = updated;
            return updated;
        }
    }

    public Device? Get(DeviceId id)
    {
        lock (_gate)
        {
            return _devices.TryGetValue(id, out var device) ? device : null;
        }
    }
}