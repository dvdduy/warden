# CLAUDE.md — Warden project briefing

> This file is read by Claude Code on every session. It contains the architecture rules,
> working conventions, and trigger phrases for this project. Follow these without being asked.

---

## What this project is

Warden is a self-healing Windows endpoint management system built as a portfolio project
targeting a Staff Engineer (.NET / Windows systems) role. A fleet of simulated devices
continuously reconciles toward a cloud-defined desired state. The engineering focus is
**distributed-systems correctness under failure** — not feature count.

Full context in:
- `docs/WARDEN_TAKEHOME.md` — the exercise spec and rubric we're building against
- `docs/WARDEN_COURSE.md` — the session-by-session build plan
- `DESIGN.md` — architecture decisions and trade-offs
- `README.md` — problem statement and milestone overview

---

## The one rule that cannot be broken

**`Warden.Core` has no I/O.**

- No HTTP clients, no database access, no file I/O, no `DateTime.UtcNow` (use `IClock`)
- No references to `Warden.ControlPlane`, `Warden.Agent`, or any external package with I/O
- Every piece of reconciliation and command logic lives here, testable in-process

If any suggestion would add I/O to `Warden.Core`, reject it and find another way.

---

## Project structure

```
warden/
  src/
    Warden.Core/           ← pure domain + reconciler (no I/O)
    Warden.ControlPlane/   ← in-memory stores + sweeper + IControlPlaneClient impl
    Warden.Agent/          ← simulated device loop
    Warden.Demo/           ← console runner: N agents, live output
  tests/
    Warden.Core.Tests/     ← all failure + concurrency paths; FakeClock lives here
  docs/                    ← planning and spec documents (read-only reference)
  CLAUDE.md                ← this file
  DESIGN.md                ← living architecture doc
  README.md
  Warden.slnx
```

**Dependency rule:**
- `Core` → nothing
- `ControlPlane` → `Core`
- `Agent` → `Core`
- `Demo` → `Core`, `ControlPlane`, `Agent`
- `Core.Tests` → `Core` only (tests the logic directly, not through a host)

---

## Domain model (source of truth)

```csharp
record DeviceId(string Value);
record CommandId(string Value);

record Device(DeviceId Id, string Hostname, ActualState Actual, DateTimeOffset LastSeen);
record DesiredState(IReadOnlyDictionary<string, string> Settings);
record ActualState(IReadOnlyDictionary<string, string> Settings);

record Command(
    CommandId Id,
    DeviceId DeviceId,
    string Action,           // e.g. "set:featureX=on"
    CommandStatus Status,
    int Attempts,
    DateTimeOffset IssuedAt,
    DateTimeOffset? AckDeadline,
    DateTimeOffset? AckedAt);

enum CommandStatus { Pending, Delivered, Acked, Failed }
```

---

## The four hard behaviors (the whole point)

Every design and implementation decision is in service of these four:

1. **Idempotent apply** — the agent tracks `HashSet<CommandId> _applied`; a duplicate command re-acks but does not re-mutate.
2. **Offline → reconnect** — no stale backlog; the returning agent reconciles against *current* desired state fresh.
3. **Duplicate delivery / duplicate acks** — `MarkAcked` on an already-`Acked` command is a no-op; terminal states never exit.
4. **No-ack → bounded retry → Failed** — the sweeper redelivers up to `MaxAttempts`, then marks `Failed`; a new command is issued on the next cycle, not a resurrection.

---

## Key interfaces (never bypass these)

```csharp
// Core abstractions
interface IClock { DateTimeOffset UtcNow { get; } }
interface ICommandStore { /* MarkDelivered, MarkAcked, MarkFailed — all idempotent */ }
interface IDeviceRepository { /* register, get, update */ }

// The transport seam — in-process now, REST in v0.2-mvp
interface IControlPlaneClient
{
    Task<DeviceId> RegisterAsync(string hostname);
    Task<DesiredState> GetDesiredStateAsync(DeviceId id);
    Task<Command?> ReportStateAsync(DeviceId id, ActualState actual);
    Task AcknowledgeAsync(DeviceId id, CommandId commandId);
}
```

`IControlPlaneClient` is why the agent never knows whether it's talking in-process or over REST.
Swapping the implementation in `v0.2-mvp` must require zero changes to `Warden.Agent` or `Warden.Core`.

---

## IClock and FakeClock

**Never use `DateTime.UtcNow` directly.** Always inject `IClock`.

```csharp
// In Warden.Core
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

// In Warden.Core.Tests
public class FakeClock : IClock
{
    private DateTimeOffset _now;
    public FakeClock(DateTimeOffset start) => _now = start;
    public DateTimeOffset UtcNow => _now;
    public void Advance(TimeSpan by) => _now += by;
}
```

Tests advance `FakeClock` instead of sleeping. Tests that `Task.Delay` are tests that don't get run.

---

## Reconciler contract

```csharp
// Pure function — no side effects, no I/O
public static IReadOnlyList<Command> Diff(
    DesiredState desired,
    ActualState actual,
    IReadOnlyList<Command> inFlight)
```

Returns commands needed to close gaps. Returns nothing when:
- The device is already compliant, OR
- A `Pending` or `Delivered` command already exists for that gap (one-in-flight-per-gap invariant)

---

## Command store transitions (guarded + idempotent)

```
Pending → Delivered   (MarkDelivered)
Delivered → Acked     (MarkAcked)      ← idempotent: duplicate ack = no-op
Delivered → Pending   (sweeper redeliver, attempts < max)
Delivered → Failed    (sweeper, attempts == max)
```

**Terminal states:** `Acked` and `Failed` never transition out, regardless of input.
**Illegal transitions** (e.g. acking a `Failed` command) are rejected without corrupting state.

---

## Working conventions

**One session = one commit.** Follow `docs/WARDEN_COURSE.md` session boundaries exactly.
Don't let one sitting bleed into the next session's scope.

**Commit message format:**
```
<type>: <what it does, not what files changed>

Types: feat | fix | test | refactor | chore | docs
Examples:
  feat: reconciliation engine with one-in-flight-per-gap invariant
  feat: agent loop with idempotent command application
  test: no-ack timeout → redeliver → Failed on FakeClock
  chore: solution skeleton + domain model
```

**Tests must cover failure paths, not happy paths only.** For every feature, ask:
- What happens if this runs twice?
- What happens if the ack never arrives?
- What happens if the device disappears mid-command?

**`dotnet build` and `dotnet test` must be green before every commit.** No exceptions.

---

## What NOT to build in v0.1-core

| Do not add | Why |
|---|---|
| REST / HTTP endpoints | Transport is `IControlPlaneClient` in-process; REST is v0.2-mvp |
| PostgreSQL or any database | In-memory only; persistence is v0.2-mvp |
| Docker / docker-compose | Added in v0.2-mvp |
| UI or dashboard | Not graded; not in scope |
| Real OS calls (BitLocker, registry) | v0.2-mvp; adds Windows-only dev requirement |
| Multi-tenancy | v0.3+ |
| Named-pipe IPC | v0.3-ipc; deserves its own milestone |

If a suggestion would add any of the above, note it in `DESIGN.md` under "What I cut" and move on.

---

## Trigger phrases

Use these in Claude Code to start a scoped session:

| Say this | What happens |
|---|---|
| `Start Warden session 1` | Solution skeleton + domain model |
| `Start Warden session 2` | Reconciliation engine (pure diff) |
| `Start Warden session 3` | Command store + guarded lifecycle transitions |
| `Start Warden session 4` | Agent loop + idempotent apply |
| `Start Warden session 5` | No-ack timeout, redelivery, offline reconcile |
| `Start Warden session 6` | Concurrency hardening + observability + demo runner |
| `Start Warden session 7` | DESIGN.md, README, CI, tag v0.1-core |

Each session ends with a commit. Do not proceed to the next session in the same sitting
unless the current one is committed and green.

---

## Current status

- [x] Repo created — `github.com/dvdduy/warden`
- [x] Planning docs in `docs/`
- [x] `README.md` and `DESIGN.md` in place
- [x] Solution skeleton (`Warden.slnx`, `src/`, `tests/`)
- [x] Session 1 — domain model + `IClock` → `chore: solution skeleton + domain model`
- [x] Session 2 — reconciler → `feat: reconciliation engine`
- [x] Session 3 — command store → `feat: command store with guarded lifecycle transitions`
- [x] Session 4 — agent loop → `feat: agent loop with idempotent command application`
- [x] Session 5 — timeout + offline → `feat: ack-timeout redelivery and safe offline reconcile`
- [x] Session 6 — concurrency + demo → `feat: concurrency test + structured logging + demo runner`
- [x] Session 7 — docs + CI → `docs: DESIGN.md + README + CI` then `git tag v0.1-core`
- [x] `v0.2-mvp` — Postgres persistence, REST transport, real BitLocker policy, compliance dashboard → `git tag v0.2-mvp`
- [x] `v0.3-ipc` — hardened named-pipe IPC, per-session user-agent lifecycle, compliance-change toast, mutual watchdog → `git tag v0.3-ipc`
