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

## Architecture (the short version)

Three components, one dependency rule: `Warden.Core` has no I/O — the entire reconciliation and command logic is a pure library, testable without HTTP or a database.

```
Warden.Agent          IControlPlaneClient seam          Warden.ControlPlane
(simulated device) ──────────────────────────────────▶  (stores + sweeper)
        │                                                       │
        └──────────── depends on ──── Warden.Core ─────────────┘
                                   (domain + reconciler)
                                          ▲
                                  Warden.Core.Tests
                                  (failure paths)
```

The `IControlPlaneClient` seam is why the transport is swappable: in-process for `v0.1-core`, REST for `v0.2-mvp` — without touching `Core`.

## Build sequence

This repo is built in milestones, each tagged, each small and complete:

| Tag | What it is | Status |
|---|---|---|
| `v0.1-core` | Reconciliation engine + simulated agent. The four hard behaviors proven by tests. No UI, no database, no real OS calls. This is the take-home-sized artifact. | ✅ Done |
| `v0.2-mvp` | One real Windows policy (BitLocker), REST transport, PostgreSQL, bare dashboard. The full compliance loop end-to-end. | ✅ Done |
| `v0.3-ipc` | User-context agent + hardened named-pipe IPC across the System↔User privilege boundary. The highest-signal Windows piece. | ✅ Done |

See [`docs/WARDEN_COURSE.md`](docs/WARDEN_COURSE.md) for the session-by-session build plan for `v0.1-core`.
See [`docs/WARDEN_COURSE_IPC.md`](docs/WARDEN_COURSE_IPC.md) and [`docs/WARDEN_IPC.md`](docs/WARDEN_IPC.md) for the `v0.3-ipc` Windows IPC milestone.
See [`docs/WARDEN_TAKEHOME.md`](docs/WARDEN_TAKEHOME.md) for the full exercise spec and rubric this is built against.

## Tech stack

- **C# / .NET 10** — the target role's stack; idiomatic throughout
- **xUnit** — tests focused on failure and concurrency paths
- **Microsoft.Extensions.Logging** — structured logs correlated by `CommandId` across the agent/control-plane boundary
- **GitHub Actions** — CI on every push and PR ([`.github/workflows/ci.yml`](.github/workflows/ci.yml))
- **Docker Compose** — one command to run the control plane (added in `v0.2-mvp`)

## Prerequisites

- **.NET 10 SDK** — everything below assumes `dotnet` is on `PATH`.
- **Docker** (for the `v0.2-mvp`/`v0.3-ipc` sections) — either Docker Desktop, or a Linux daemon
  reachable through WSL, in which case prefix the `docker`/`docker compose` commands below with
  `wsl` (e.g. `wsl docker compose up --build`).
- **Windows** — `Warden.Agent`, `Warden.UserAgent`, and their test projects (`Warden.Ipc.Tests`,
  `Warden.Agent.Tests`) call real Windows APIs (named-pipe ACLs, `WindowsIdentity`, WTS session
  lookups) and only run correctly on Windows, though they compile anywhere. `Warden.Core` and
  `Warden.ControlPlane` are fully cross-platform. CI reflects this split: see
  [`.github/workflows/ci.yml`](.github/workflows/ci.yml) for the exact commands run on each OS.

## Running it

### v0.1-core demo

```bash
git clone https://github.com/dvdduy/warden.git
cd warden
dotnet test tests/Warden.Core.Tests  # the pure reconciliation core -- green with zero setup
dotnet run --project src/Warden.Demo # watch reconciliation happen live
```

Running plain `dotnet test` from the repo root picks up the whole solution, including the
Postgres-backed `v0.2-mvp` tests and the Windows-only `v0.3-ipc` tests -- see
[`.github/workflows/ci.yml`](.github/workflows/ci.yml) for how CI runs the full suite split across
a Linux job (with a real Postgres) and a Windows job.

`Warden.Demo` runs four scripted scenarios end-to-end with no human input, each printing what's
happening as it goes:

1. **Duplicate delivery applies once** — the same command id delivered three times mutates state once.
2. **No ack → bounded retry → `Failed`** — a command that's never acked redelivers up to `MaxAttempts`, fails cleanly, and the next reconciliation cycle issues a fresh command for the still-open gap.
3. **Offline → reconnect** — a device that never ran a cycle reconciles to *current* desired state on its first connect, even if that state changed while it was "offline."
4. **A live fleet** — 40 simulated agents converge against one control plane concurrently, with a colored compliant/non-compliant board and a final `FleetHealth` snapshot.

### v0.2-mvp stack

```bash
docker compose up --build
# no Docker Desktop? if the daemon runs in WSL instead: wsl docker compose up --build
```

That starts PostgreSQL and the ASP.NET Core control plane at:

- Dashboard: `http://localhost:5000/dashboard`
- Health: `http://localhost:5000/health`

For a local-safe BitLocker demo without admin rights or changing disk encryption, run the agent
in fake BitLocker mode against the compose control plane:

```powershell
$env:Agent__UseFakeBitLocker = "true"
$env:Agent__FakeBitLockerEnabled = "false"
$env:Agent__ControlPlaneBaseAddress = "http://localhost:5000"
dotnet run --project src/Warden.Agent
```

The first cycle reports `bitlocker.enabled=false`, receives the normal remediation command,
flips fake state in memory, and the next cycle reports green. The real mode uses `manage-bde`
and should be run elevated or as the Windows Service/LocalSystem. (The agent logs a loud warning
at startup if fake mode is on — it's a config flag, not a build switch, and should never be left
set on a real managed device.)

### v0.3-ipc demo script

`v0.3-ipc` adds the user-context boundary: `Warden.Agent` can keep running as the service-side
remediator, while `Warden.UserAgent` runs in the logged-on user's desktop session and receives
notifications over a hardened named pipe.

For the safe local demo, keep using fake BitLocker mode:

```powershell
$env:Agent__UseFakeBitLocker = "true"
$env:Agent__FakeBitLockerEnabled = "false"
$env:Agent__ControlPlaneBaseAddress = "http://localhost:5000"
dotnet run --project src/Warden.Agent
```

Demo flow:

1. Start the `v0.2-mvp` compose stack and open `http://localhost:5000/dashboard`.
2. Start `Warden.Agent` in fake BitLocker mode.
3. Let the first cycle report `bitlocker.enabled=false`; the control plane issues the normal
   remediation command.
4. The agent remediates, acks, and routes `ComplianceChanged { Rule = bitlocker.enabled,
   Status = Compliant }` over the session pipe.
5. `Warden.UserAgent` receives the message and attempts a Windows toast in the interactive session.
6. Kill the `Warden.UserAgent` process; the service detects the exit and relaunches it with bounded
   backoff.
7. Re-trigger the drift/remediation flow and confirm the relaunched user-agent still receives the
   next compliance-change notification.

**Steps 4-7 need `Warden.Agent` running as `LocalSystem`, not a plain `dotnet run`.** Launching
`Warden.UserAgent` into a specific session (`CreateProcessAsUserLauncher`) needs `SeTcbPrivilege`,
which only a process actually running as `LocalSystem` holds -- an ordinary user's `dotnet run`
never has it. Two ways to see the real flow:

- **Install it as an actual Windows Service** (elevated): `sc create WardenAgent binPath="<path
  to Warden.Agent.exe>"` then `sc start WardenAgent`. This is the real deployment path and what
  `SERVICE_CONTROL_SESSIONCHANGE` delivery depends on.
- **Run interactively as SYSTEM** for a quick local look, e.g. via
  [PsExec](https://learn.microsoft.com/sysinternals/downloads/psexec): `psexec -s -i dotnet run
  --project src/Warden.Agent -- --Agent:UseFakeBitLocker=true`.

Running `dotnet run --project src/Warden.Agent` directly (as in steps 1-3, or the `v0.2-mvp`
section above) still demos the full compliance loop -- reporting, drift detection, remediation --
but session launch fails gracefully with a single logged line
(`WTSQueryUserToken failed for session 1`, Win32 error 1314) instead of a crash, and steps 4-7
simply won't happen. That failure mode is itself deliberate and covered by
`SessionAgentManagerTests`.

Security checks to show alongside the demo:

```powershell
dotnet test tests\Warden.Ipc.Tests\Warden.Ipc.Tests.csproj
dotnet test tests\Warden.Agent.Tests\Warden.Agent.Tests.csproj
```

The IPC tests prove ping/pong framing, service-pushed compliance messages, ACL creation, and
peer-session rejection. The agent tests prove per-session lifecycle, respawn after unexpected
user-agent exit, bounded backoff, and crash-loop circuit breaking.

## Design decisions

Full reasoning in [`DESIGN.md`](DESIGN.md). The short version:

**At-least-once delivery + idempotent apply, not exactly-once.** Exactly-once delivery across a distributed system is not achievable without trade-offs that are worse than the problem. At-least-once with idempotency keys gives the same correctness guarantee with simpler, more debuggable mechanics.

**Pure reconciliation core.** `Reconciler.Diff(desired, actual, inFlight)` is a pure function — deterministic, no I/O, testable in isolation. The control plane just applies its output.

**In-memory first.** `v0.1-core` uses no database. That is a deliberate scope decision, not an oversight. PostgreSQL is added in `v0.2-mvp` once the logic is proven correct. See `DESIGN.md` for the persistence trade-off.

---

*Status: `v0.3-ipc` complete — the `v0.2-mvp` compliance loop now has a hardened per-session user-agent boundary for desktop notifications.*
