# Warden

[![CI](https://github.com/dvdduy/warden/actions/workflows/ci.yml/badge.svg)](https://github.com/dvdduy/warden/actions/workflows/ci.yml)

A self-healing Windows endpoint management system — a simulated fleet of devices that continuously reconciles toward a cloud-defined desired state, detects drift, and corrects itself without human intervention.

Built as a portfolio project targeting a **Staff Engineer (.NET / Windows systems)** role. Designed to demonstrate distributed-systems correctness, Windows service engineering, and production-grade thinking — not feature count.

---

## The problem

A company has thousands of Windows laptops. Each one has settings that should be in a known-good state — encryption enabled, a security policy applied, a service running. Over time, machines drift: a user changes a setting, a process crashes, an update reverts a config. Nobody knows until something goes wrong or a helpdesk ticket appears.

Today that means: **reactive discovery → manual remediation → one machine at a time.**

## How Warden solves it

Every device runs a lightweight agent. Every 60 seconds the agent asks a central control plane: *"what should I look like?"* It compares that against what it actually looks like, reports the gap, and receives a command to close it. The agent applies the command and reports back. The control plane tracks every command's lifecycle until it is confirmed applied.

**The result:** drift is detected within one cycle. Remediation is automatic. No ticket, no human, no waiting.

```
Device                        Control Plane
  │                                │
  │──── report actual state ──────▶│
  │                                │ compares actual vs desired
  │◀─── command: set featureX=on ──│ (if gap exists)
  │                                │
  │ apply command                  │
  │ (idempotent — safe to retry)   │
  │                                │
  │──── ack ──────────────────────▶│ command lifecycle: complete
  │                                │
  │ (next cycle: compliant,        │
  │  no new command issued)        │
```

## What makes this hard

The interesting engineering is not the happy path. It is what happens when things fail:

- A command delivered twice must apply **exactly once** — not twice.
- A device that goes offline and comes back must reconcile against *current* desired state — not replay a stale backlog.
- A command the device never acknowledges must redeliver a bounded number of times, then land in a visible `Failed` state — never hang forever.
- Duplicate acks from a flaky network must not corrupt command state.

These four behaviors are the core of the system. Everything else is scaffolding around them.

## Architecture

```
Session N (interactive user)
┌──────────────────┐
│ Warden.UserAgent │
└──────────────────┘
          │
          │  named pipe -- ACL'd + peer-verified
          ▼
┌────────────────────────────────┐
│ Warden.Agent                   │
│ (Windows Service, LocalSystem) │
└────────────────────────────────┘
                 │
                 │  REST (IControlPlaneClient)
                 ▼
┌─────────────────────────┐
│ Warden.ControlPlane.Api │
│ (ASP.NET Core)          │
└─────────────────────────┘
             │
             ▼
┌────────────┐
│ PostgreSQL │
└────────────┘
```

One dependency rule holds it together: `Warden.Core` has no I/O — the entire reconciliation and command logic is a pure library, testable without HTTP or a database. `Warden.Agent` and `Warden.ControlPlane` both depend on it and nothing else touches it directly; the `IControlPlaneClient` seam is why the REST boundary is swappable without touching `Core`.

## Tech stack

- **C# / .NET 10** — the target role's stack; idiomatic throughout
- **xUnit** — tests focused on failure and concurrency paths
- **Microsoft.Extensions.Logging** — structured logs correlated by `CommandId` across the agent/control-plane boundary
- **GitHub Actions** — CI on every push and PR ([`.github/workflows/ci.yml`](.github/workflows/ci.yml))
- **Docker Compose** — one command to run the full Postgres-backed control plane

## Running it

```bash
git clone https://github.com/dvdduy/warden.git
cd warden
dotnet test tests/Warden.Core.Tests  # the pure reconciliation core -- green with zero setup
dotnet run --project src/Warden.Demo # watch reconciliation happen live
```

That's the fastest path to seeing it work — `Warden.Demo` runs four scripted scenarios live: duplicate delivery applying exactly once, no-ack → bounded retry → `Failed`, an offline device reconciling to current desired state, and 40 agents converging concurrently.

The full system — the Postgres-backed control plane, the real BitLocker policy, and the Windows IPC/user-agent demo — needs Docker and/or Windows; see **[`docs/RUNNING.md`](docs/RUNNING.md)** for prerequisites and step-by-step instructions for every layer.

## Design decisions

Full reasoning in [`DESIGN.md`](DESIGN.md). The short version:

**At-least-once delivery + idempotent apply, not exactly-once.** Exactly-once delivery across a distributed system is not achievable without trade-offs that are worse than the problem. At-least-once with idempotency keys gives the same correctness guarantee with simpler, more debuggable mechanics.

**Pure reconciliation core.** `Reconciler.Diff(desired, actual, inFlight)` is a pure function — deterministic, no I/O, testable in isolation. The control plane just applies its output.

**In-memory first.** The reconciliation core originally shipped with no database at all. That was a deliberate scope decision, not an oversight — Postgres was added once the logic was proven correct. See `DESIGN.md` for the persistence trade-off.
