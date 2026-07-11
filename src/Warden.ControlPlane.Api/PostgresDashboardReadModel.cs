using System.Text.Json;
using Npgsql;
using Warden.Core;

namespace Warden.ControlPlane.Api;

public sealed class PostgresDashboardReadModel : IDashboardReadModel
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresDashboardReadModel(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public IReadOnlyList<DashboardComplianceRow> GetRows()
    {
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, hostname, actual_state, last_seen
            FROM devices
            ORDER BY hostname
            """;

        var rows = new List<DashboardComplianceRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var actualState = DeserializeSettings(reader.GetString(2));
            actualState.TryGetValue("bitlocker.enabled", out var actual);

            rows.Add(new DashboardComplianceRow(
                DeviceId: reader.GetString(0),
                Hostname: reader.GetString(1),
                Rule: "bitlocker.enabled",
                Expected: "true",
                Actual: actual,
                LastSeen: reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return rows;
    }

    public void RecordDevice(Device device)
    {
        // PostgreSQL is already the source of truth for dashboard reads.
    }

    public void RecordActualState(DeviceId id, ActualState actual, DateTimeOffset lastSeen)
    {
        // PostgreSQL is already the source of truth for dashboard reads.
    }

    private static Dictionary<string, string> DeserializeSettings(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
}
