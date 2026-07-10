# WARDEN_COURSE.md — building `v0.1-core`

> **Target:** the take-home-sized reconciliation service (`WARDEN_TAKEHOME.md`) — the reusable heart
> the MVP and every extension sit on. Seven sessions, ~1 hour each (~6–8h total, matching the budget).
> **Format:** each session is **Learn → Build → Commit → Earn**. Every session ends with running,
> committed code. No session leaves the repo broken overnight.
> **Stack:** C#/.NET (current LTS, .NET 8+), xUnit for tests. The .NET version isn't load-bearing here.

## How to run this course

- One session per sitting. Do them in order — each depends on the last.
- **Trigger phrase:** in the WARDEN project, say **"Start Warden session N"** and I'll walk you through that session's Learn material, then pair with you on the Build, ending at the commit.
- Don't skip the commit. The commit history *is* a hiring signal — incremental commits with real messages beat one final dump.
- Don't let an early session balloon. The whole point of `v0.1-core` is *a small thing done completely*.

## The finish line

By the end you have a tagged `v0.1-core`: a control plane + simulated agent that provably handle the four hard behaviors, tests that target the failure paths, a `DESIGN.md` a reviewer nods at, and green CI. The demo you can show: *deliver a command twice → it applies once; drop an ack → it redelivers then fails cleanly; take a device offline → it reconciles on return without replaying stale commands.*

### The four hard behaviors → which session lands each

| # | Behavior | Lands in |
|---|---|---|
| 1 | At-least-once delivery + idempotent apply | Session 4 |
| 2 | Offline → reconnect without stale replay | Session 5 |
| 3 | Duplicate delivery / duplicate acks | Sessions 3 & 4 |
| 4 | No-ack → timeout → bounded retry → Failed | Session 5 |

---

## Session 1 — Solution skeleton + domain model

**Learn.** Why `Warden.Core` holds no I/O (so the hard logic is testable without HTTP or a DB). Records vs classes for domain types. Modelling a state machine as an `enum` + guarded transitions rather than loose booleans. Why an `IClock` abstraction is non-negotiable for testing timeouts without real waiting.

**Build.**
- Create the solution and four projects: `Warden.Core`, `Warden.ControlPlane`, `Warden.Agent`, `Warden.Core.Tests`. `Core` references nothing; the others reference `Core`.
- Domain types in `Core`: `DeviceId`, `Device`, `DesiredState` (a settings map, e.g. `featureX = on`), `ActualState`, `Command` (`Id`, `DeviceId`, `Action`, `Status`, `Attempts`, timestamps), and `enum CommandStatus { Pending, Delivered, Acked, Failed }`.
- `IClock` + `SystemClock`, plus a `FakeClock` in the test project.

**Commit.** `chore: solution skeleton + domain model` — solution builds, one smoke test green.

**Earn.** The vocabulary of the system is now code; the command state machine from the design exists as a type. *Interview line:* "I modelled command state as a guarded enum so illegal transitions are unrepresentable."

---

## Session 2 — Reconciliation engine (the pure diff)

**Learn.** Reconciliation as a *pure function*: `desired × actual × in-flight → commands`. Why purity makes it trivially testable and idempotent by construction. The key invariant: **at most one in-flight command per gap** (this is what prevents duplicate-command storms).

**Build.**
- `Reconciler.Diff(desired, actual, inFlight)` in `Core` → returns the command(s) needed to close gaps. No I/O, deterministic.
- It returns **nothing** when the device is already compliant, **and nothing** when a command for that gap is already in flight.
- Tests: gap → exactly one command; compliant → none; gap with an in-flight command → none.

**Commit.** `feat: reconciliation engine with one-in-flight-per-gap invariant`.

**Earn.** The core value logic exists and is provably correct in isolation — the single thing reviewers weight most. *Interview line:* "Reconciliation is a pure function; the control plane just applies its output."

---

## Session 3 — Command store + guarded lifecycle transitions

**Learn.** Why the store is the *authority* on command state. Guarding transitions (you can't ack a `Failed` command; acking an already-`Acked` command is a no-op, not an error). Thread-safety, because many agents hit the store concurrently.

**Build.**
- `ICommandStore` + a thread-safe in-memory implementation.
- Transitions `MarkDelivered / MarkAcked / MarkFailed`, each **idempotent and guarded**: duplicate acks collapse to one effect; illegal transitions are rejected without corrupting state.
- Tests: duplicate ack → single terminal state; illegal transition → no-op/rejected; concurrent acks on one command → still one effect.

**Commit.** `feat: command store with guarded, idempotent lifecycle transitions`.

**Earn.** Hard behavior #3 (control-plane side) done — duplicate acks can't corrupt state. *Interview line:* "Idempotent transitions mean the network can duplicate my acks and nothing breaks."

---

## Session 4 — Agent loop + idempotent apply ★ the spine

**Learn.** The `IControlPlaneClient` seam (in-process now, REST later — this is why the take-home never forced a transport choice). Agent-side dedup: an *applied-command-id set* so re-delivering a command re-acks but doesn't re-mutate. Simulating the device as a mutable settings dict.

**Build.**
- `IControlPlaneClient` port + an in-process implementation wired to `Warden.ControlPlane`.
- `Agent` holding local `ActualState` and a `HashSet<CommandId> _applied`. Loop: register → fetch desired → report actual → receive command → `Apply` (**if id already in `_applied`, skip the mutation**) → ack.
- `ControlPlane` orchestrator ties store + reconciler + devices together.
- Tests: deliver the *same* command twice → setting changes **once**; full cycle drives a non-compliant device to compliant end-to-end.

**Commit.** `feat: agent loop with idempotent command application`.

**Earn.** The marquee behavior: a duplicate command applies exactly once. This is the demo and the spine of the whole exercise. *Interview line:* "At-least-once delivery plus an idempotent apply is how I get correctness without pretending exactly-once exists."

---

## Session 5 — No-ack timeout, redelivery, and safe offline reconcile

**Learn.** Ack/visibility timeouts and bounded retries. The timeout **sweeper** as a background loop driven by `IClock` (so tests advance a `FakeClock` instead of waiting). Why "offline" needs no special queue: the returning agent just reconciles against *current* desired-vs-actual — it never replays a stale local backlog. Superseding: if desired changes while a command is in flight, the old one is cancelled and a fresh one issued.

**Build.**
- Ack timeout on `Delivered` commands; sweeper redelivers while `attempts < max`, else marks `Failed`.
- Offline simulation: an agent that skips N cycles then returns and converges correctly.
- Supersede logic: changing desired state cancels the in-flight command for that gap and issues a new one.
- Tests (all on `FakeClock`, no real sleeping): no-ack → redeliver up to max → `Failed`; offline → reconnect → converges without applying a stale/destructive command; desired changes mid-flight → old command superseded.

**Commit.** `feat: ack-timeout redelivery, bounded retries, and safe offline reconcile`.

**Earn.** All four hard behaviors provably handled — the exercise is functionally complete. *Interview line:* "A command that's never acked redelivers a bounded number of times then lands in a visible `Failed` state — it never hangs forever."

---

## Session 6 — Concurrency hardening, observability, demo runner

**Learn.** Testing under contention (does anything double-apply or get stuck with 100+ agents?). Structured logging with a correlation id that follows a command across the agent↔control-plane boundary — the thing that lets you debug machines you can't touch. A runnable demo beats a described one.

**Build.**
- Concurrency test: spin up 100+ simulated agents against one control plane; assert no double-apply, no stuck commands, every gap converges.
- Structured logging (`Microsoft.Extensions.Logging`) with a per-command correlation id; a simple health signal.
- `Warden.Demo` console (or the ControlPlane host) that runs N agents and prints reconciliation happening live — red → green with no human.
- *Optional:* a thin REST facade over the client seam, to prove the transport swaps in without touching `Core`.

**Commit.** `feat: concurrency test + structured logging + demo runner`.

**Earn.** The production-mindedness signal, plus a live demo you can screen-share in an interview. *Interview line:* "Correlation ids let me trace one command end-to-end across the boundary."

---

## Session 7 — Capstone: DESIGN.md, README, CI, tag `v0.1-core`

**Learn.** What a reviewer actually reads for (judgment, honesty, trade-offs — not feature count). Writing a design doc that reasons about *why* and about *scale*. Commit-history and README hygiene.

**Build.**
- `DESIGN.md`: the delivery-guarantee choice and why not exactly-once; how each of the four hard behaviors is handled; transport/storage trade-offs; **what breaks first at 1,000,000 devices** and what you'd redesign; what you cut and why.
- `README.md` written as a design doc — opens with the problem and your approach, *then* how to run.
- GitHub Actions CI: build + test on push, green badge.
- Final pass; make sure `docker compose up` (or the documented run command) works clean.

**Commit.** `docs: DESIGN.md + README + CI`, then **`git tag v0.1-core`**.

**Earn.** A tagged, defensible, take-home-sized artifact — the *small thing done completely* you can hand any reviewer, or lift patterns from when a real take-home lands. *Interview line:* the whole repo, which you can now walk through line by line.

---

## After `v0.1-core`

The natural next tags, each its own short arc (I can write `WARDEN_COURSE_MVP.md` when you're ready):

- **`v0.2-mvp`** — swap the simulated setting for a real one (BitLocker), the in-proc seam for REST, add PostgreSQL and a bare dashboard. The full `WARDEN_MVP.md` loop.
- **`v0.3-ipc`** — the user-context agent + hardened named-pipe IPC. The highest-signal Windows piece for this role (Winlogon, System vs User context, privilege boundary).
- Then real-time push (SignalR), a second policy, multi-tenancy — following `WARDEN_PROJECT.md`.

> This course assumes the design we mapped out — the package architecture, the command state machine, and the reconciliation sequence. If you'd like that packaged as a standalone `WARDEN_CORE_DESIGN.md` to sit beside this course in the project, say the word.
