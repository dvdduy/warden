using System.Text.Json;
using Npgsql;
using Warden.Core;

namespace Warden.ControlPlane.Postgres;

/// <summary>
/// PostgreSQL-backed IDeviceRepository. Desired state lives in its own `desired_states`
/// table rather than a column on `devices` — mirroring InMemoryDeviceRepository's two
/// separate dictionaries, so SetDesiredState works even for a device that hasn't
/// registered yet, exactly like the in-memory implementation.
/// </summary>
public sealed class PostgresDeviceRepository : IDeviceRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresDeviceRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public Device Register(DeviceId id, string hostname, IClock clock)
    {
        using var connection = _dataSource.OpenConnection();
        using var cmd = connection.CreateCommand();
        // Idempotent upsert: a repeat registration only bumps last_seen, never resets
        // hostname or actual_state -- matches InMemoryDeviceRepository.Register exactly.
        cmd.CommandText = """
            INSERT INTO devices (id, hostname, actual_state, last_seen)
            VALUES (@id, @hostname, '{}'::jsonb, @last_seen)
            ON CONFLICT (id) DO UPDATE SET last_seen = EXCLUDED.last_seen
            RETURNING id, hostname, actual_state, last_seen
            """;
        cmd.Parameters.AddWithValue("id", id.Value);
        cmd.Parameters.AddWithValue("hostname", hostname);
        cmd.Parameters.AddWithValue("last_seen", clock.UtcNow);

        using var reader = cmd.ExecuteReader();
        reader.Read();
        return ReadDevice(reader);
    }

    public DesiredState GetDesiredState(DeviceId id)
    {
        using var connection = _dataSource.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT settings FROM desired_states WHERE device_id = @device_id";
        cmd.Parameters.AddWithValue("device_id", id.Value);

        var json = cmd.ExecuteScalar() as string;
        return json is null ? DesiredState.Empty : new DesiredState(DeserializeSettings(json));
    }

    public void SetDesiredState(DeviceId id, DesiredState desired)
    {
        using var connection = _dataSource.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO desired_states (device_id, settings)
            VALUES (@device_id, @settings::jsonb)
            ON CONFLICT (device_id) DO UPDATE SET settings = EXCLUDED.settings
            """;
        cmd.Parameters.AddWithValue("device_id", id.Value);
        cmd.Parameters.AddWithValue("settings", SerializeSettings(desired.Settings));
        cmd.ExecuteNonQuery();
    }

    public Device ReportActualState(DeviceId id, ActualState actual, IClock clock)
    {
        using var connection = _dataSource.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE devices SET actual_state = @actual_state::jsonb, last_seen = @last_seen
            WHERE id = @id
            RETURNING id, hostname, actual_state, last_seen
            """;
        cmd.Parameters.AddWithValue("id", id.Value);
        cmd.Parameters.AddWithValue("actual_state", SerializeSettings(actual.Settings));
        cmd.Parameters.AddWithValue("last_seen", clock.UtcNow);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new KeyNotFoundException(
                $"Device {id} has not registered. Call Register before reporting state.");
        }

        return ReadDevice(reader);
    }

    public Device? Get(DeviceId id)
    {
        using var connection = _dataSource.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, hostname, actual_state, last_seen FROM devices WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id.Value);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDevice(reader) : null;
    }

    private static string SerializeSettings(IReadOnlyDictionary<string, string> settings) =>
        JsonSerializer.Serialize(settings);

    private static Dictionary<string, string> DeserializeSettings(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

    private static Device ReadDevice(NpgsqlDataReader reader) => new(
        Id: new DeviceId(reader.GetString(0)),
        Hostname: reader.GetString(1),
        Actual: new ActualState(DeserializeSettings(reader.GetString(2))),
        LastSeen: reader.GetFieldValue<DateTimeOffset>(3));
}
