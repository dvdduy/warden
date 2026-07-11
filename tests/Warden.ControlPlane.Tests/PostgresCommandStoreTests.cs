using Warden.Core;
using Warden.ControlPlane.Postgres;
using Xunit;

namespace Warden.ControlPlane.Tests;

/// <summary>
/// Proves PostgresCommandStore honors the exact same guarantees as InMemoryCommandStore
/// (see Warden.Core.Tests.InMemoryCommandStoreTests) against a real database: duplicate
/// acks collapse to a no-op, illegal transitions are rejected without corrupting state,
/// and concurrent duplicate acks against one row still produce exactly one effective
/// transition. This is the "seam-proving" this session is about -- the guard logic lives
/// in Warden.Core either way; only the persistence mechanics differ.
/// </summary>
[Collection("Postgres")]
public class PostgresCommandStoreTests
{
    private readonly PostgresFixture _fixture;
    private static readonly DeviceId Device1 = new("dev_1");

    public PostgresCommandStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    private static Command NewPendingCommand(string action = "set:featureX=on") =>
        new(
            Id: CommandId.NewId(),
            DeviceId: Device1,
            Action: action,
            Status: CommandStatus.Pending,
            Attempts: 0,
            IssuedAt: DateTimeOffset.UtcNow,
            AckDeadline: null,
            AckedAt: null);

    /// <summary>
    /// Postgres `timestamptz` has microsecond resolution, coarser than a DateTimeOffset
    /// tick (100ns) -- truncate a locally-constructed expected value the same way
    /// PostgresCommandStore truncates before persisting, so comparisons against a value
    /// that round-tripped through the database aren't chasing a sub-microsecond remainder
    /// that was never going to survive.
    /// </summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);

    [Fact]
    public void Add_then_Get_returns_the_same_command()
    {
        var store = new PostgresCommandStore(_fixture.DataSource);
        var command = NewPendingCommand();

        // Compare against Add()'s return value, not the original `command` local --
        // Add() truncates IssuedAt to Postgres's microsecond resolution before persisting
        // and returns the truncated version; the untruncated original never round-trips.
        var added = store.Add(command);
        var fetched = store.Get(command.Id);

        Assert.Equal(added, fetched);
    }

    [Fact]
    public void Get_on_unknown_id_returns_null()
    {
        var store = new PostgresCommandStore(_fixture.DataSource);

        Assert.Null(store.Get(CommandId.NewId()));
    }

    [Fact]
    public void Add_with_duplicate_id_throws()
    {
        var store = new PostgresCommandStore(_fixture.DataSource);
        var command = NewPendingCommand();
        store.Add(command);

        Assert.Throws<InvalidOperationException>(() => store.Add(command));
    }

    [Fact]
    public void MarkDelivered_moves_Pending_to_Delivered_and_increments_Attempts()
    {
        var store = new PostgresCommandStore(_fixture.DataSource);
        var command = store.Add(NewPendingCommand());
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        var delivered = store.MarkDelivered(command.Id, deadline);

        Assert.Equal(CommandStatus.Delivered, delivered.Status);
        Assert.Equal(1, delivered.Attempts);
        Assert.Equal(TruncateToMicroseconds(deadline), delivered.AckDeadline);
    }

    [Fact]
    public void Duplicate_ack_is_a_no_op_and_does_not_change_AckedAt()
    {
        var store = new PostgresCommandStore(_fixture.DataSource);
        var command = store.Add(NewPendingCommand());
        store.MarkDelivered(command.Id, DateTimeOffset.UtcNow.AddSeconds(30));

        var firstAckAt = DateTimeOffset.UtcNow.AddSeconds(5);
        var firstAck = store.MarkAcked(command.Id, firstAckAt);

        var secondAckAt = DateTimeOffset.UtcNow.AddSeconds(9);
        var secondAck = store.MarkAcked(command.Id, secondAckAt);

        Assert.Equal(CommandStatus.Acked, secondAck.Status);
        // Compare against firstAck.AckedAt (already round-tripped through Postgres, so
        // truncated to microsecond precision) rather than the raw firstAckAt input --
        // Postgres timestamptz has coarser resolution than a DateTimeOffset tick, so the
        // untruncated input never exactly equals what's actually stored.
        Assert.Equal(firstAck.AckedAt, secondAck.AckedAt); // unchanged -- first ack wins
        Assert.Equal(firstAck, secondAck);
    }

    [Fact]
    public void Acking_a_Failed_command_throws_and_state_is_untouched()
    {
        var store = new PostgresCommandStore(_fixture.DataSource);
        var command = store.Add(NewPendingCommand());
        store.MarkDelivered(command.Id, DateTimeOffset.UtcNow.AddSeconds(30));
        store.MarkFailed(command.Id);

        var ex = Assert.Throws<InvalidCommandTransitionException>(
            () => store.MarkAcked(command.Id, DateTimeOffset.UtcNow));

        Assert.Equal(CommandStatus.Failed, ex.From);
        Assert.Equal(CommandStatus.Acked, ex.To);
        Assert.Equal(CommandStatus.Failed, store.Get(command.Id)!.Status);
    }

    [Fact]
    public void MarkPendingForRedelivery_moves_Delivered_back_to_Pending_and_clears_deadline()
    {
        var store = new PostgresCommandStore(_fixture.DataSource);
        var command = store.Add(NewPendingCommand());
        store.MarkDelivered(command.Id, DateTimeOffset.UtcNow.AddSeconds(30));

        var redelivered = store.MarkPendingForRedelivery(command.Id);

        Assert.Equal(CommandStatus.Pending, redelivered.Status);
        Assert.Null(redelivered.AckDeadline);
    }

    [Fact]
    public void GetInFlight_returns_only_Pending_and_Delivered_commands_for_the_device()
    {
        var store = new PostgresCommandStore(_fixture.DataSource);

        var pending = store.Add(NewPendingCommand("set:featureA=on"));
        var delivered = store.Add(NewPendingCommand("set:featureB=on"));
        store.MarkDelivered(delivered.Id, DateTimeOffset.UtcNow.AddSeconds(30));

        var acked = store.Add(NewPendingCommand("set:featureC=on"));
        store.MarkDelivered(acked.Id, DateTimeOffset.UtcNow.AddSeconds(30));
        store.MarkAcked(acked.Id, DateTimeOffset.UtcNow);

        var otherDevice = new Command(
            CommandId.NewId(), new DeviceId("dev_2"), "set:featureX=on",
            CommandStatus.Pending, 0, DateTimeOffset.UtcNow, null, null);
        store.Add(otherDevice);

        var inFlight = store.GetInFlight(Device1);

        Assert.Equal(2, inFlight.Count);
        Assert.Contains(inFlight, c => c.Id == pending.Id);
        Assert.Contains(inFlight, c => c.Id == delivered.Id);
    }

    [Fact]
    public void GetDeliveredPastDeadline_returns_only_overdue_Delivered_commands()
    {
        var store = new PostgresCommandStore(_fixture.DataSource);
        var now = DateTimeOffset.UtcNow;

        var overdue = store.Add(NewPendingCommand("set:featureA=on"));
        store.MarkDelivered(overdue.Id, now.AddSeconds(-1)); // deadline already passed

        var notYetOverdue = store.Add(NewPendingCommand("set:featureB=on"));
        store.MarkDelivered(notYetOverdue.Id, now.AddSeconds(300));

        var pastDeadline = store.GetDeliveredPastDeadline(now);

        var result = Assert.Single(pastDeadline);
        Assert.Equal(overdue.Id, result.Id);
    }

    [Fact]
    public void Concurrent_duplicate_acks_produce_exactly_one_effective_transition()
    {
        var store = new PostgresCommandStore(_fixture.DataSource);
        var command = store.Add(NewPendingCommand());
        store.MarkDelivered(command.Id, DateTimeOffset.UtcNow.AddSeconds(30));

        const int threadCount = 20;
        var results = new Command[threadCount];
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, i =>
        {
            try
            {
                results[i] = store.MarkAcked(command.Id, DateTimeOffset.UtcNow.AddSeconds(i));
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions); // duplicate acks must never throw, even under real DB row-lock contention
        Assert.All(results, r => Assert.Equal(CommandStatus.Acked, r.Status));

        var distinctAckedAtValues = results.Select(r => r.AckedAt).Distinct().Count();
        Assert.Equal(1, distinctAckedAtValues); // exactly one transition was "real"; the rest no-op'd
    }
}
