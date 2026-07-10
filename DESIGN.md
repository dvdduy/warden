# DESIGN.md — Warden `v0.1-core`

> **Status:** living document — sections marked `TODO` are filled in as each session completes.
> Reasoning for decisions already made is captured now; empirical sections (performance, failure
> observations) are added once the code exists to observe.

---

## Problem statement

A fleet of Windows devices drifts out of a known-good state over time. No mechanism exists to
detect drift automatically or correct it without a helpdesk ticket. Warden is the minimal system
that makes drift visible within one polling cycle and corrects it without human intervention.

`v0.1-core` proves the correctness model in-process with a simulated device. Real OS calls,
a database, and a network transport are deliberately deferred to `v0.2-mvp`.

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

TODO — fill in after Session 5 (you'll have hands-on experience with the in-memory limits by then).
Sketch: the first thing to break is the in-memory command store under concurrent load. The sweeper
becomes a bottleneck. Consider:
- Partition command state by device shard.
- Move the sweeper to a competing-consumer pattern across worker instances.
- Replace the in-memory store with PostgreSQL + a `FOR UPDATE SKIP LOCKED` pattern for the sweeper.
- Add jitter to check-in intervals to avoid thundering-herd on reconnect.
- Separate the read path (desired-state serving) from the write path (state reporting, acks) —
  they have very different scaling profiles.

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

## What I'd do next (beyond `v0.1-core`)

TODO — fill in after Session 7 once you've lived with the constraints of the in-memory model.

---

## Observability notes

TODO — fill in after Session 6 (structured logging + correlation IDs).

---

## Open questions

TODO — note any design questions that came up during building that you haven't resolved yet.
These are good interview talking points: "here's a decision I'm still not sure about and here's
the trade-off I see."
