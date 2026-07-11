# DESIGN.md — Warden

> **Status:** `v0.1-core` and `v0.2-mvp` complete. Written incrementally as each session landed
> rather than backfilled at the end — the empirical sections reflect what tests and integration
> work actually showed, not speculation.

---

## Problem statement

A fleet of Windows devices drifts out of a known-good state over time. No mechanism exists to
detect drift automatically or correct it without a helpdesk ticket. Warden is the minimal system
that makes drift visible within one polling cycle and corrects it without human intervention.

`v0.1-core` proved the correctness model in-process with a simulated device. `v0.2-mvp` keeps
that core unchanged while swapping in PostgreSQL, REST, a real BitLocker read/remediation path,
and a bare dashboard.

---

## Core decisions

### 1. At-least-once delivery + idempotent apply — not exactly-once

Exactly-once delivery across a distributed boundary is not achievable without two-phase commit or
equivalent, which trades one class of problem (duplicates) for a worse one (availability and
complexity). The practical alternative is:

- The control plane delivers commands **at least once** — it will retry until acknowledged.
- The agent applies a command **idempotently** — it tracks which command IDs it has already
  applied and skips the mutation on a duplicate, re-sending the ack.

This gives the same observable outcome (each command's effect applied exactly once) with simpler,
more debuggable mechanics and no distributed transaction.

**Rejected alternative:** exactly-once via a transactional outbox or distributed lock. Added
complexity outweighs the benefit at this scale; the idempotency approach is more operationally
transparent.

### 2. Pure reconciliation core — `Warden.Core` has no I/O

`Reconciler.Diff(desired, actual, inFlight) → commands` is a pure function: deterministic, no
side effects, no HTTP, no database. The control plane applies its output; it does not embed
business logic.

**Why this matters:** the hardest logic in the system is testable in-process without mocks,
containers, or network. Tests run in milliseconds and exercise the logic directly.

**Rejected alternative:** embedding reconciliation logic inside the API controllers. Untestable
without spinning up the full stack, and couples transport concerns to domain logic.

### 3. At most one in-flight command per gap

The reconciler never issues a second command for a gap that already has a `Pending` or
`Delivered` command. This prevents command storms when a device is slow to respond and the
reconciler runs multiple times before an ack arrives.

**Implication:** if desired state changes while a command is in flight, the old command is
superseded (cancelled) and a new one issued for the updated gap.

### 4. IClock abstraction for all time-dependent logic

All timeout and retry logic depends on `IClock` rather than `DateTime.UtcNow` directly.
Tests use `FakeClock` to advance time without real waiting. This keeps the full no-ack
redelivery cycle testable in milliseconds.

**Rejected alternative:** `Task.Delay` in tests. Slow, flaky, and tests that sleep are tests
that don't get run.

### 5. In-memory storage for `v0.1-core`

No database in this milestone. Device state, desired state, and the command store are all
in-memory dictionaries behind interfaces (`IDeviceRepository`, `ICommandStore`). The interfaces
are the persistence boundary — PostgreSQL implementations drop in for `v0.2-mvp` without
touching domain logic.

**Trade-off:** state does not survive a control plane restart. Acceptable for `v0.1-core`
because the goal is proving the correctness model, not persistence. See "What I'd do next."

### 6. Transport: in-process via IControlPlaneClient for `v0.1-core`

The agent communicates with the control plane through the `IControlPlaneClient` port. In
`v0.1-core` this is a direct in-process call. In `v0.2-mvp` a REST implementation replaces it
without any change to `Warden.Core` or `Warden.Agent`.

**Why this ordering:** the interesting engineering is in the delivery guarantees and reconciliation
logic, not in HTTP plumbing. Proving correctness in-process first means the logic is right before
a network is involved.

### 7. `v0.2-mvp`: persistence, transport, and one real policy

`v0.2-mvp` proves the seams from `v0.1-core` rather than rewriting the core:

- `PostgresCommandStore` and `PostgresDeviceRepository` implement the same interfaces as the
  in-memory stores. The command lifecycle rules still live in `Warden.Core`; PostgreSQL adds
  durability and row-level locking, not new business logic.
- `RestControlPlaneClient` implements the same `IControlPlaneClient` port the simulated agent
  already used. The agent loop does not know whether calls are in-process or HTTP+JSON.
- The Windows agent adds `IActualStateProvider` and `ICommandExecutor` outside `Warden.Core`.
  Real mode shells out to `manage-bde`; fake mode exercises the same reporting/remediation
  pipeline locally without admin rights or disk-encryption changes.
- The dashboard is intentionally a read-only table. It exists to make the red-to-green loop
  visible for a demo, not to become a product surface.

### 8. BitLocker is asynchronous

`manage-bde -on C:` starts remediation, but encryption/protection state is not a perfect
instantaneous boolean. The agent therefore acks the command after the command invocation succeeds,
then reports newly observed BitLocker state on the next poll. In fake mode this appears as:

1. report `bitlocker.enabled=false` -> command issued and executed,
2. next cycle reports `bitlocker.enabled=true` -> dashboard turns green.

That timing is a feature of the real domain, not a bug in reconciliation. Compliance is based on
observed state, not on wishfully assuming the command's effect is visible immediately.

---

## The four hard behaviors — design

### 1. At-least-once delivery + idempotent apply

- Control plane: commands move `Pending → Delivered` when served; a background sweeper checks for
  `Delivered` commands past their ack deadline and redelivers (incrementing `Attempts`).
- Agent: maintains `HashSet<CommandId> _applied`. On receiving a command: if id is in the set,
  skip the mutation and re-send the ack. If not, apply the mutation, add to the set, send the ack.
- Result: the same command id delivered N times produces one mutation and N acks, all idempotent.

### 2. Offline → reconnect

No special queue or backlog on the agent. When a device reconnects it runs its normal loop:
fetch current desired state → compare to current actual state → report → receive command if gap
exists. The gap is computed fresh against *current* desired state, not a stale snapshot from
before the device went offline. This means:

- If desired state changed while the device was offline, the device reconciles to the *new*
  desired state, not the old one.
- Old in-flight commands for that device are superseded when desired state changes; they will not
  be redelivered after reconnect.

### 3. Duplicate delivery / duplicate acks

- **Duplicate command delivery:** handled by the agent's `_applied` set (see #1).
- **Duplicate acks:** the command store's `MarkAcked` transition is idempotent — acking an
  already-`Acked` command is a no-op. The store never transitions out of a terminal state
  (`Acked`, `Failed`), regardless of how many duplicate signals arrive.

### 4. No-ack → bounded retry → Failed

The ack timeout sweeper runs on `IClock`. For each `Delivered` command past its deadline:

- If `Attempts < MaxAttempts`: increment `Attempts`, reset deadline, mark `Pending` for
  redelivery on next agent cycle.
- If `Attempts == MaxAttempts`: mark `Failed` (terminal). The gap will be re-evaluated by the
  reconciler on the next cycle — a *new* command is issued, not a resurrection of the failed one.

`MaxAttempts` and the ack deadline are configurable. Defaults: `MaxAttempts = 3`,
`AckDeadline = 30s` (compressed to milliseconds in tests via `FakeClock`).

---

## Package structure

```
Warden.Core            pure domain + reconciler (no I/O)
Warden.ControlPlane    hosts Core; in-memory stores; sweeper background service
Warden.Agent           simulated device loop; IControlPlaneClient consumer
Warden.Demo            console runner: N agents, live reconciliation output
Warden.Core.Tests      unit + integration tests; all failure and concurrency paths
```

Dependency rule: `Core` references nothing. `ControlPlane` and `Agent` reference `Core`.
`Tests` references `Core` directly — no need to go through a host to test the logic.

---

## Data model (`v0.1-core` — in-memory)

```csharp
record DeviceId(string Value);

record Device(
    DeviceId Id,
    string Hostname,
    ActualState Actual,
    DateTimeOffset LastSeen);

record DesiredState(IReadOnlyDictionary<string, string> Settings);
record ActualState(IReadOnlyDictionary<string, string> Settings);

record Command(
    CommandId Id,
    DeviceId DeviceId,
    string Action,          // e.g. "set:featureX=on"
    CommandStatus Status,
    int Attempts,
    DateTimeOffset IssuedAt,
    DateTimeOffset? AckDeadline,
    DateTimeOffset? AckedAt);

enum CommandStatus { Pending, Delivered, Acked, Failed }
```

---

## What I would do differently at 1,000,000 devices

`v0.1-core`'s concurrency test (`ConcurrencyTests`, Session 6) runs 200 simulated agents against
one `InMemoryCommandStore`/`InMemoryDeviceRepository` pair behind a single `lock`. That's the
honest ceiling of this design: it proves *correctness* under contention, not throughput at scale.
After `v0.2-mvp`, the first-order conclusions still hold, but the concrete pressure points are
clearer. At 1,000,000 devices, in order of what breaks first:

1. **The single-lock in-memory store becomes the bottleneck first.** Every `ReportStateAndGetNewCommands`
   call and every sweep does a read-modify-write under one `lock (_gate)`. That's fine for hundreds
   of concurrent callers; at fleet scale it serializes the whole system on one mutex.
   → Partition command/device state by shard (e.g. hash of `DeviceId`), each shard with its own
   lock or its own store instance, so unrelated devices stop contending with each other.

2. **The ack-timeout sweeper is a single sequential scan (`GetDeliveredPastDeadline`) over every
   command in the store.** At 1M devices with even a few in-flight commands each, that's a
   multi-million-row scan on every sweep tick.
   → Move to a competing-consumer pattern: multiple sweeper workers, each claiming a shard or a
   batch via `FOR UPDATE SKIP LOCKED` once the store is PostgreSQL-backed (see decision #5), so
   no single process scans the whole fleet.

3. **PostgreSQL removes the restart-loss problem, but not the fleet-scale scan problem.** The MVP
   schema is intentionally direct: one `commands` table, one `devices` table, simple indexes.
   At 1M devices I would first measure `ReportStateAndGetNewCommands` latency, command insert/update
   contention, and the overdue-command sweeper query before choosing between partitioning, queueing,
   or read replicas.

4. **Polling every device on a fixed interval creates thundering-herd reconnect traffic** — if the
   control plane restarts or a network partition heals, every agent's next poll lands in the same
   window.
   → Add jitter to the poll interval per device (already trivial: `Agent`/`AgentHost` own the delay
   between cycles, not `Core`).

5. **Read (desired-state serving) and write (state reporting, acks) have very different scaling
   profiles** — desired state changes rarely and is read on every poll; actual-state/ack traffic is
   constant and write-heavy.
   → Split them onto separate paths/stores (e.g. desired state cached aggressively or served from a
   read replica) once real load numbers justify it — premature at `v0.1-core`'s scale.

---

## What I cut for `v0.1-core` and why

| Cut | Reason | Where it lands |
|---|---|---|
| Real OS calls (BitLocker) | Adds Windows-only dev requirement; the point of `v0.1-core` is the delivery guarantees, not the enforcement | `v0.2-mvp` |
| REST / HTTP transport | Correctness is independent of transport; in-process is faster to prove and easier to test | `v0.2-mvp` |
| PostgreSQL | Persistence is not needed to prove the reconciliation model | `v0.2-mvp` |
| Dashboard / UI | Not graded; wrong thing to spend time on | `v0.2-mvp` |
| Multi-tenancy | Complexity without payoff until the model is proven | `v0.3+` |
| Named-pipe IPC / User-context agent | The highest-signal Windows piece — deserves its own milestone | `v0.3-ipc` |
| SignalR / real-time push | Polling is correct and sufficient for the correctness story | `v0.3+` |

---

## What I'd do next (beyond `v0.2-mvp`)

In roadmap order:

- **`v0.3-ipc`**: the user-context agent and hardened named-pipe IPC across the System↔User privilege
  boundary. The highest-Windows-signal piece of this whole project, deliberately deferred until the
  correctness model underneath it is proven.
- **Real-time push (SignalR)**, a second policy beyond BitLocker, and multi-tenancy — once the
  single-policy, polling-based loop is validated end-to-end with real devices.
- **Distributed tracing** (see Observability notes below) once there's a real network hop to trace
  across, not just an in-process call.

## Observability notes

Session 6 added structured logging (`Microsoft.Extensions.Logging`) to `ControlPlane` and
`AckTimeoutSweeper`, both defaulting to `NullLogger` so nothing changes for existing callers/tests
that don't pass one in. Every log line is keyed on `CommandId`:

```
Command {CommandId} issued for device {DeviceId}: {Action}
Command {CommandId} marked Delivered (attempt {Attempts})
Command {CommandId} acked
Command {CommandId} ack timeout — redelivering (attempt {Attempts}/{MaxAttempts})
Command {CommandId} exhausted {MaxAttempts} delivery attempts — marking Failed
Command {CommandId} superseded — desired state changed before it was acked
```

`CommandId` is the correlation id: because it's generated once (`Reconciler.Diff`) and carried
unchanged through delivery, redelivery, and ack/fail, grepping one id's log lines reconstructs a
command's entire life across the agent↔control-plane boundary — without a distributed tracing
system, which would be overkill for an in-process transport. `Warden.Demo` wires a console logger
provider into this at composition-root level (`Warden.ControlPlane`/`Warden.Agent` only depend on
the `ILogger` *abstraction*, never a concrete sink — same seam discipline as everything else here).

`FleetHealth` (`ControlPlane.GetHealthSnapshot()`) is the simplest possible health signal: a
point-in-time count of commands by status. It answers "is anything stuck?" (`InFlightCommands`)
and "is anything actively failing?" (`FailedCommands`) without needing a metrics backend. It's a
full scan at query time — fine at `v0.1-core` scale, listed as the first thing to replace with
running counters at real scale (see "What I would do differently at 1,000,000 devices").

**What's still missing:** a trace/span id distinct from `CommandId` for non-command requests such as
enrollment, dashboard reads, and health checks. Metrics emission (counters/histograms) is also still
absent; fine for a demo, not for an on-call dashboard.

## Open questions

Genuine unresolved trade-offs, not settled decisions:

1. **Superseded and exhausted-retries commands share one terminal `Failed` status.** `CommandStatus`
   deliberately stays at four states (`Pending`, `Delivered`, `Acked`, `Failed` — see the domain
   model pinned in `CLAUDE.md`), so "desired state changed before this command was acked" and "this
   command was delivered `MaxAttempts` times and never acked" are indistinguishable from the status
   alone. An operator debugging "why did this command fail" has to reason from context (was desired
   state changed around the same time?) rather than reading it off the record. A fifth status, or a
   `FailureReason` field, would remove the ambiguity — I chose not to add one for `v0.1-core` because
   it's a hard-to-repeat mistake with the *guarded transition* model (every new status is another
   row in `CommandStatusTransitions.IsLegal` to get right), and the ambiguity is real but low-cost
   at this scale.
2. **The reconciler's gap-to-command mapping is a string convention (`"set:{key}={value}"`), parsed
   back apart by `Reconciler.FindSuperseded` and `Agent.Apply`.** It works, and keeping `Command`
   simple (`Action` as a plain string) was deliberate — the domain isn't the point. But it means the
   parsing logic is duplicated in two places instead of `Command` carrying a structured `(Key,
   Value)` target directly. I'd revisit this the moment a second action shape (not just `set:`)
   shows up beyond the current `set:bitlocker.enabled=true` / `enable-bitlocker` pair.
3. **The ack-timeout sweeper is currently something the caller has to remember to invoke** —
   `ControlPlane.SweepAckTimeouts()` is not itself scheduled; `Warden.Demo` runs it on a
   `Task.Run` loop, but nothing in `Warden.ControlPlane` enforces that a host actually does this in
   production. A real deployment needs this made structural (a hosted background service that can't
   be forgotten) rather than left as "call this periodically" documentation.
