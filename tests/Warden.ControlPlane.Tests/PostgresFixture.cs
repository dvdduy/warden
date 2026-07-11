using Npgsql;
using Warden.ControlPlane.Postgres;

namespace Warden.ControlPlane.Tests;

/// <summary>
/// Shared Postgres connection for the whole test run: creates the schema once, and gives
/// each test a Reset() to truncate between tests instead of paying schema-creation cost
/// per test. Requires a running Postgres reachable at WARDEN_TEST_DB (or the
/// docker-compose default) -- see docker-compose.yml at the repo root:
/// `docker compose up -d postgres`.
/// </summary>
public sealed class PostgresFixture : IDisposable
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=warden;Username=warden;Password=warden";

    public NpgsqlDataSource DataSource { get; }

    public PostgresFixture()
    {
        var connectionString = Environment.GetEnvironmentVariable("WARDEN_TEST_DB") ?? DefaultConnectionString;
        DataSource = NpgsqlDataSource.Create(connectionString);
        PostgresSchema.EnsureCreated(DataSource);
    }

    /// <summary>Wipes all tables. Call at the start of every test for isolation.</summary>
    public void Reset()
    {
        using var connection = DataSource.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "TRUNCATE devices, desired_states, commands";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => DataSource.Dispose();
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
