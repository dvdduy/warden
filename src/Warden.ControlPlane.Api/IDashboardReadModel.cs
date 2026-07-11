using Warden.Core;

namespace Warden.ControlPlane.Api;

public interface IDashboardReadModel
{
    IReadOnlyList<DashboardComplianceRow> GetRows();

    void RecordDevice(Device device);

    void RecordActualState(DeviceId id, ActualState actual, DateTimeOffset lastSeen);
}
