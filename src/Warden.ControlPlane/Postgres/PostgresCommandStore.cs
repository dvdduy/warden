using Npgsql;
using Warden.Core;

namespace Warden.ControlPlane.Postgres;

/// <summary>
/// PostgreSQL-backed ICommandStore. Queries here are deliberately dumb — every transition
/// decision (is this a no-op? is this legal? should it throw?) is delegated to the exact
/// same <see cref="CommandStatusTransitions"/> predicates InMemoryCommandStore uses. This
/// store's only job is turning "here's the current row, here's the target status" into a
/// `SELECT ... FOR UPDATE` + `UPDATE` pair; it does not reimplement the state machine in
/// SQL. That's what keeps the guard rules living in exactly one place (Warden.Core) no
/// matter how many storage backends exist.
/// </summary>
public sealed class PostgresCommandStore : ICommandStore
{
    private const string SelectColumns =
        "id, device_id, action, status, attempts, issued_at, ack_deadline, acked_at";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresCommandStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public Command Add(Command command)
    {
        // Postgres `timestamptz` has microsecond resolution; .NET DateTimeOffset has
        // 100ns ticks. Truncate before persisting AND before returning, so the Command
        // handed back here is byte-for-byte what a subsequent Get() would return --
        // otherwise a caller comparing the two would see a spurious mismatch on the
        // sub-microsecond remainder that never actually round-trips through the DB.
        command = TruncateTimestamps(command);

        using var connection = _dataSource.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO commands (id, device_id, action, status, attempts, issued_at, ack_deadline, acked_at)
            VALUES (@id, @device_id, @action, @status, @attempts, @issued_at, @ack_deadline, @acked_at)
            """;
        BindCommandParameters(cmd, command);

        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"Command {command.Id} already exists.");
        }

        return command;
    }

    public Command? Get(CommandId id)
    {
        using var connection = _dataSource.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM commands WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id.Value);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCommand(reader) : null;
    }

    public IReadOnlyList<Command> GetInFlight(DeviceId deviceId)
    {
        using var connection = _dataSource.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectColumns} FROM commands
            WHERE device_id = @device_id AND status IN ('Pending', 'Delivered')
            """;
        cmd.Parameters.AddWithValue("device_id", deviceId.Value);

        return ReadAll(cmd);
    }

    public IReadOnlyList<Command> GetAll()
    {
        using var connection = _dataSource.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM commands";

        return ReadAll(cmd);
    }

    public IReadOnlyList<Command> GetDeliveredPastDeadline(DateTimeOffset now)
    {
        using var connection = _dataSource.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectColumns} FROM commands
            WHERE status = 'Delivered' AND ack_deadline IS NOT NULL AND ack_deadline <= @now
            """;
        cmd.Parameters.AddWithValue("now", now);

        return ReadAll(cmd);
    }

    public Command MarkDelivered(CommandId id, DateTimeOffset ackDeadline) =>
        Transition(id, CommandStatus.Delivered, current => current with
        {
            Status = CommandStatus.Delivered,
            Attempts = current.Attempts + 1,
            AckDeadline = ackDeadline
        });

    public Command MarkAcked(CommandId id, DateTimeOffset ackedAt) =>
        Transition(id, CommandStatus.Acked, current => current with
        {
            Status = CommandStatus.Acked,
            AckedAt = ackedAt
        });

    public Command MarkFailed(CommandId id) =>
        Transition(id, CommandStatus.Failed, current => current with
        {
            Status = CommandStatus.Failed
        });

    public Command MarkPendingForRedelivery(CommandId id) =>
        Transition(id, CommandStatus.Pending, current => current with
        {
            Status = CommandStatus.Pending,
            AckDeadline = null
        });

    /// <summary>
    /// Shared guarded-transition path for every Mark* method. `SELECT ... FOR UPDATE`
    /// takes a row lock so a concurrent transition on the same command blocks until this
    /// one commits — the database-level equivalent of InMemoryCommandStore's in-process
    /// `lock (_gate)`. The guard decision itself (no-op / legal / illegal) is exactly the
    /// same Warden.Core predicates the in-memory store uses.
    /// </summary>
    private Command Transition(CommandId id, CommandStatus to, Func<Command, Command> apply)
    {
        using var connection = _dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = $"SELECT {SelectColumns} FROM commands WHERE id = @id FOR UPDATE";
        select.Parameters.AddWithValue("id", id.Value);

        Command current;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new KeyNotFoundException($"Command {id} does not exist.");
            }

            current = ReadCommand(reader);
        }

        if (CommandStatusTransitions.IsNoOp(current.Status, to))
        {
            transaction.Commit();
            return current;
        }

        if (!CommandStatusTransitions.IsLegal(current.Status, to))
        {
            transaction.Rollback();
            throw new InvalidCommandTransitionException(current.Status, to);
        }

        // Same precision truncation as Add() -- apply() sets AckDeadline/AckedAt from
        // in-memory DateTimeOffset values (full tick precision); truncate before both
        // persisting and returning so this call's result matches what Get() would show.
        var updated = TruncateTimestamps(apply(current));

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE commands
            SET status = @status, attempts = @attempts, ack_deadline = @ack_deadline, acked_at = @acked_at
            WHERE id = @id
            """;
        update.Parameters.AddWithValue("id", updated.Id.Value);
        update.Parameters.AddWithValue("status", updated.Status.ToString());
        update.Parameters.AddWithValue("attempts", updated.Attempts);
        update.Parameters.AddWithValue("ack_deadline", (object?)updated.AckDeadline ?? DBNull.Value);
        update.Parameters.AddWithValue("acked_at", (object?)updated.AckedAt ?? DBNull.Value);
        update.ExecuteNonQuery();

        transaction.Commit();
        return updated;
    }

    /// <summary>
    /// Rounds every timestamp on a Command down to microsecond precision -- Postgres
    /// `timestamptz`'s native resolution, one order of magnitude coarser than a .NET
    /// DateTimeOffset tick (100ns). Without this, a Command built in memory and a Command
    /// read back from the database differ by a sub-microsecond remainder that can never
    /// round-trip, which breaks equality checks and (as the concurrency test caught)
    /// makes two reads of the *same* persisted row look like two different values.
    /// </summary>
    private static Command TruncateTimestamps(Command command) => command with
    {
        IssuedAt = TruncateToMicroseconds(command.IssuedAt),
        AckDeadline = command.AckDeadline is { } deadline ? TruncateToMicroseconds(deadline) : null,
        AckedAt = command.AckedAt is { } ackedAt ? TruncateToMicroseconds(ackedAt) : null,
    };

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);

    private static void BindCommandParameters(NpgsqlCommand cmd, Command command)
    {
        cmd.Parameters.AddWithValue("id", command.Id.Value);
        cmd.Parameters.AddWithValue("device_id", command.DeviceId.Value);
        cmd.Parameters.AddWithValue("action", command.Action);
        cmd.Parameters.AddWithValue("status", command.Status.ToString());
        cmd.Parameters.AddWithValue("attempts", command.Attempts);
        cmd.Parameters.AddWithValue("issued_at", command.IssuedAt);
        cmd.Parameters.AddWithValue("ack_deadline", (object?)command.AckDeadline ?? DBNull.Value);
        cmd.Parameters.AddWithValue("acked_at", (object?)command.AckedAt ?? DBNull.Value);
    }

    private static IReadOnlyList<Command> ReadAll(NpgsqlCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var results = new List<Command>();
        while (reader.Read())
        {
            results.Add(ReadCommand(reader));
        }

        return results;
    }

    private static Command ReadCommand(NpgsqlDataReader reader) => new(
        Id: new CommandId(reader.GetString(0)),
        DeviceId: new DeviceId(reader.GetString(1)),
        Action: reader.GetString(2),
        Status: Enum.Parse<CommandStatus>(reader.GetString(3)),
        Attempts: reader.GetInt32(4),
        IssuedAt: reader.GetFieldValue<DateTimeOffset>(5),
        AckDeadline: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        AckedAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));
}
