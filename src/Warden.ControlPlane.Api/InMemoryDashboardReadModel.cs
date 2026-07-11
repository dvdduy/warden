using Warden.Core;

namespace Warden.ControlPlane.Api;

public sealed class InMemoryDashboardReadModel : IDashboardReadModel
{
    private readonly object _gate = new();
    private readonly Dictionary<DeviceId, Device> _devices = new();

    public IReadOnlyList<DashboardComplianceRow> GetRows()
    {
        lock (_gate)
        {
            return _devices.Values
                .OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase)
                .Select(ToRow)
                .ToList();
        }
    }

    public void RecordDevice(Device device)
    {
        lock (_gate)
        {
            _devices[device.Id] = device;
        }
    }

    public void RecordActualState(DeviceId id, ActualState actual, DateTimeOffset lastSeen)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var existing))
            {
                return;
            }

            _devices[id] = existing with { Actual = actual, LastSeen = lastSeen };
        }
    }

    private static DashboardComplianceRow ToRow(Device device)
    {
        device.Actual.Settings.TryGetValue("bitlocker.enabled", out var actual);

        return new DashboardComplianceRow(
            DeviceId: device.Id.Value,
            Hostname: device.Hostname,
            Rule: "bitlocker.enabled",
            Expected: "true",
            Actual: actual,
            LastSeen: device.LastSeen);
    }
}
