using Warden.Core;
using Xunit;

namespace Warden.Core.Tests;

public class InMemoryCommandStoreTests
{
    private static readonly DeviceId Device1 = new("dev_1");

    private static Command NewPendingCommand(FakeClock clock, string action = "set:featureX=on") =>
        new(
            Id: CommandId.NewId(),
            DeviceId: Device1,
            Action: action,
            Status: CommandStatus.Pending,
            Attempts: 0,
            IssuedAt: clock.UtcNow,
            AckDeadline: null,
            AckedAt: null);

    // ---- basic add / get ----

    [Fact]
    public void Add_then_Get_returns_the_same_command()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = NewPendingCommand(clock);

        store.Add(command);
        var fetched = store.Get(command.Id);

        Assert.Equal(command, fetched);
    }

    [Fact]
    public void Get_on_unknown_id_returns_null()
    {
        var store = new InMemoryCommandStore();

        Assert.Null(store.Get(CommandId.NewId()));
    }

    [Fact]
    public void Add_with_duplicate_id_throws()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = NewPendingCommand(clock);
        store.Add(command);

        Assert.Throws<InvalidOperationException>(() => store.Add(command));
    }

    // ---- legal forward transitions ----

    [Fact]
    public void MarkDelivered_moves_Pending_to_Delivered_and_increments_Attempts()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = store.Add(NewPendingCommand(clock));
        var deadline = clock.UtcNow.AddSeconds(30);

        var delivered = store.MarkDelivered(command.Id, deadline);

        Assert.Equal(CommandStatus.Delivered, delivered.Status);
        Assert.Equal(1, delivered.Attempts);
        Assert.Equal(deadline, delivered.AckDeadline);
    }

    [Fact]
    public void MarkAcked_moves_Delivered_to_Acked_and_sets_AckedAt()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = store.Add(NewPendingCommand(clock));
        store.MarkDelivered(command.Id, clock.UtcNow.AddSeconds(30));

        var ackedAt = clock.UtcNow.AddSeconds(5);
        var acked = store.MarkAcked(command.Id, ackedAt);

        Assert.Equal(CommandStatus.Acked, acked.Status);
        Assert.Equal(ackedAt, acked.AckedAt);
    }

    [Fact]
    public void MarkFailed_moves_Delivered_to_Failed()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = store.Add(NewPendingCommand(clock));
        store.MarkDelivered(command.Id, clock.UtcNow.AddSeconds(30));

        var failed = store.MarkFailed(command.Id);

        Assert.Equal(CommandStatus.Failed, failed.Status);
    }

    // ---- duplicate / no-op transitions (the core of this session) ----

    [Fact]
    public void Duplicate_ack_is_a_no_op_and_does_not_change_AckedAt()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = store.Add(NewPendingCommand(clock));
        store.MarkDelivered(command.Id, clock.UtcNow.AddSeconds(30));

        var firstAckAt = clock.UtcNow.AddSeconds(5);
        var firstAck = store.MarkAcked(command.Id, firstAckAt);

        // A second ack arrives late from a retried/duplicated network call.
        var secondAckAt = clock.UtcNow.AddSeconds(9);
        var secondAck = store.MarkAcked(command.Id, secondAckAt);

        Assert.Equal(CommandStatus.Acked, secondAck.Status);
        Assert.Equal(firstAckAt, secondAck.AckedAt); // unchanged — first ack wins, no re-mutation
        Assert.Equal(firstAck, secondAck);
    }

    [Fact]
    public void Duplicate_fail_is_a_no_op()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = store.Add(NewPendingCommand(clock));
        store.MarkDelivered(command.Id, clock.UtcNow.AddSeconds(30));
        var firstFail = store.MarkFailed(command.Id);

        var secondFail = store.MarkFailed(command.Id);

        Assert.Equal(CommandStatus.Failed, secondFail.Status);
        Assert.Equal(firstFail, secondFail);
    }

    [Fact]
    public void Redelivering_an_already_Delivered_command_at_the_same_call_is_treated_as_no_op_by_status_check()
    {
        // Note: MarkDelivered is only a true no-op if status is ALREADY Delivered before
        // the call. This documents that calling it twice in a row does NOT double-increment
        // in the sense of silently ignoring the second call — Delivered -> Delivered is a
        // no-op per CommandStatusTransitions, so Attempts must NOT bump again.
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = store.Add(NewPendingCommand(clock));

        var first = store.MarkDelivered(command.Id, clock.UtcNow.AddSeconds(30));
        var second = store.MarkDelivered(command.Id, clock.UtcNow.AddSeconds(60));

        Assert.Equal(1, first.Attempts);
        Assert.Equal(1, second.Attempts); // did NOT bump to 2 — this is a no-op, not a redelivery
        Assert.Equal(first.AckDeadline, second.AckDeadline); // unchanged, first call wins
    }

    // ---- illegal transitions ----

    [Fact]
    public void Acking_a_Failed_command_throws()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = store.Add(NewPendingCommand(clock));
        store.MarkDelivered(command.Id, clock.UtcNow.AddSeconds(30));
        store.MarkFailed(command.Id);

        var ex = Assert.Throws<InvalidCommandTransitionException>(
            () => store.MarkAcked(command.Id, clock.UtcNow));

        Assert.Equal(CommandStatus.Failed, ex.From);
        Assert.Equal(CommandStatus.Acked, ex.To);

        // And state is untouched by the rejected attempt.
        Assert.Equal(CommandStatus.Failed, store.Get(command.Id)!.Status);
    }

    [Fact]
    public void Failing_an_Acked_command_throws()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = store.Add(NewPendingCommand(clock));
        store.MarkDelivered(command.Id, clock.UtcNow.AddSeconds(30));
        store.MarkAcked(command.Id, clock.UtcNow);

        Assert.Throws<InvalidCommandTransitionException>(() => store.MarkFailed(command.Id));
        Assert.Equal(CommandStatus.Acked, store.Get(command.Id)!.Status);
    }

    [Fact]
    public void Acking_a_Pending_command_that_was_never_Delivered_throws()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = store.Add(NewPendingCommand(clock));

        Assert.Throws<InvalidCommandTransitionException>(
            () => store.MarkAcked(command.Id, clock.UtcNow));
    }

    [Fact]
    public void Transition_on_unknown_command_id_throws_KeyNotFound()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();

        Assert.Throws<KeyNotFoundException>(() => store.MarkAcked(CommandId.NewId(), clock.UtcNow));
    }

    // ---- GetInFlight ----

    [Fact]
    public void GetInFlight_returns_only_Pending_and_Delivered_commands_for_the_device()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();

        var pending = store.Add(NewPendingCommand(clock, "set:featureA=on"));
        var delivered = store.Add(NewPendingCommand(clock, "set:featureB=on"));
        store.MarkDelivered(delivered.Id, clock.UtcNow.AddSeconds(30));

        var acked = store.Add(NewPendingCommand(clock, "set:featureC=on"));
        store.MarkDelivered(acked.Id, clock.UtcNow.AddSeconds(30));
        store.MarkAcked(acked.Id, clock.UtcNow);

        var failed = store.Add(NewPendingCommand(clock, "set:featureD=on"));
        store.MarkDelivered(failed.Id, clock.UtcNow.AddSeconds(30));
        store.MarkFailed(failed.Id);

        var otherDevice = new Command(
            CommandId.NewId(), new DeviceId("dev_2"), "set:featureX=on",
            CommandStatus.Pending, 0, clock.UtcNow, null, null);
        store.Add(otherDevice);

        var inFlight = store.GetInFlight(Device1);

        Assert.Equal(2, inFlight.Count);
        Assert.Contains(inFlight, c => c.Id == pending.Id);
        Assert.Contains(inFlight, c => c.Id == delivered.Id);
    }

    // ---- concurrency ----

    [Fact]
    public void Concurrent_duplicate_acks_produce_exactly_one_effective_transition()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = store.Add(NewPendingCommand(clock));
        store.MarkDelivered(command.Id, clock.UtcNow.AddSeconds(30));

        const int threadCount = 50;
        var results = new Command[threadCount];
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, i =>
        {
            try
            {
                // Every thread acks with a slightly different timestamp; only the
                // genuinely first one to acquire the lock should "win."
                results[i] = store.MarkAcked(command.Id, clock.UtcNow.AddSeconds(i));
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions); // duplicate acks must never throw
        Assert.All(results, r => Assert.Equal(CommandStatus.Acked, r.Status));

        // Every thread must observe the SAME AckedAt — i.e. exactly one ack was the
        // "real" transition and every other one collapsed to that same no-op result.
        var distinctAckedAtValues = results.Select(r => r.AckedAt).Distinct().Count();
        Assert.Equal(1, distinctAckedAtValues);

        var finalState = store.Get(command.Id)!;
        Assert.Equal(CommandStatus.Acked, finalState.Status);
    }

    [Fact]
    public void Concurrent_mixed_ack_and_fail_never_corrupts_state_into_a_non_terminal_or_double_terminal()
    {
        var store = new InMemoryCommandStore();
        var clock = new FakeClock();
        var command = store.Add(NewPendingCommand(clock));
        store.MarkDelivered(command.Id, clock.UtcNow.AddSeconds(30));

        var successCount = 0;
        var failureCount = 0;

        Parallel.For(0, 100, i =>
        {
            try
            {
                if (i % 2 == 0)
                {
                    store.MarkAcked(command.Id, clock.UtcNow);
                }
                else
                {
                    store.MarkFailed(command.Id);
                }
                Interlocked.Increment(ref successCount);
            }
            catch (InvalidCommandTransitionException)
            {
                // Expected: whichever terminal state wins first, the other type of
                // transition becomes illegal (Acked -> Failed or Failed -> Acked
                // are both rejected) rather than silently corrupting state.
                Interlocked.Increment(ref failureCount);
            }
        });

        var finalState = store.Get(command.Id)!;
        Assert.True(finalState.Status is CommandStatus.Acked or CommandStatus.Failed);
        Assert.True(CommandStatusTransitions.IsTerminal(finalState.Status));
        Assert.Equal(100, successCount + failureCount);
    }
}