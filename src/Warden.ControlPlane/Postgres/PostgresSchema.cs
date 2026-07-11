using Npgsql;

namespace Warden.ControlPlane.Postgres;

/// <summary>
/// The `v0.2-mvp` schema behind PostgresCommandStore/PostgresDeviceRepository. Desired
/// state lives in its own table (<c>desired_states</c>), decoupled from <c>devices</c> —
/// deliberately mirroring InMemoryDeviceRepository's two separate dictionaries, so
/// SetDesiredState doesn't require a device to have registered first, exactly like the
/// in-memory implementation it must behave identically to.
/// </summary>
public static class PostgresSchema
{
    public const string CreateTablesSql = """
        CREATE TABLE IF NOT EXISTS devices (
            id            TEXT PRIMARY KEY,
            hostname      TEXT NOT NULL,
            actual_state  JSONB NOT NULL DEFAULT '{}'::jsonb,
            last_seen     TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE IF NOT EXISTS desired_states (
            device_id     TEXT PRIMARY KEY,
            settings      JSONB NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE TABLE IF NOT EXISTS commands (
            id            TEXT PRIMARY KEY,
            device_id     TEXT NOT NULL,
            action        TEXT NOT NULL,
            status        TEXT NOT NULL,
            attempts      INT NOT NULL DEFAULT 0,
            issued_at     TIMESTAMPTZ NOT NULL,
            ack_deadline  TIMESTAMPTZ NULL,
            acked_at      TIMESTAMPTZ NULL
        );

        CREATE INDEX IF NOT EXISTS idx_commands_device_id ON commands(device_id);
        CREATE INDEX IF NOT EXISTS idx_commands_status_ack_deadline ON commands(status, ack_deadline);
        """;

    /// <summary>Idempotent — safe to call on every startup (and from test fixtures).</summary>
    public static void EnsureCreated(NpgsqlDataSource dataSource)
    {
        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = CreateTablesSql;
        command.ExecuteNonQuery();
    }
}
