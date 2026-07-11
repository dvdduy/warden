# Running Warden

Full run instructions for every layer of the system. Each section below builds on the last, but
all of it runs against the current code on `master` — no separate checkout needed.

See the [README](../README.md) for the one-minute overview and architecture, and
[`DESIGN.md`](../DESIGN.md) for the reasoning behind each decision.

## Prerequisites

- **.NET 10 SDK** — everything below assumes `dotnet` is on `PATH`.
- **Docker** (for the Postgres/IPC sections) — either Docker Desktop, or a Linux daemon reachable
  through WSL, in which case prefix the `docker`/`docker compose` commands below with `wsl` (e.g.
  `wsl docker compose up --build`).
- **Windows** — `Warden.Agent`, `Warden.UserAgent`, and their test projects (`Warden.Ipc.Tests`,
  `Warden.Agent.Tests`) call real Windows APIs (named-pipe ACLs, `WindowsIdentity`, WTS session
  lookups) and only run correctly on Windows, though they compile anywhere. `Warden.Core` and
  `Warden.ControlPlane` are fully cross-platform. CI reflects this split: see
  [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) for the exact commands run on each OS.

## Reconciliation core (in-memory, zero setup)

The base of the system: a pure reconciliation engine, an in-memory control plane, and a simulated
fleet proving the four hard behaviors by test. See [`WARDEN_COURSE.md`](WARDEN_COURSE.md) for the
session-by-session build plan and [`WARDEN_TAKEHOME.md`](WARDEN_TAKEHOME.md) for the exercise spec
it's built against.

```bash
git clone https://github.com/dvdduy/warden.git
cd warden
dotnet test tests/Warden.Core.Tests  # the pure reconciliation core -- green with zero setup
dotnet run --project src/Warden.Demo # watch reconciliation happen live
```

Running plain `dotnet test` from the repo root picks up the whole solution, including the
Postgres-backed control-plane tests and the Windows-only IPC tests -- see
[`.github/workflows/ci.yml`](../.github/workflows/ci.yml) for how CI runs the full suite split
across a Linux job (with a real Postgres) and a Windows job.

`Warden.Demo` runs four scripted scenarios end-to-end with no human input, each printing what's
happening as it goes:

1. **Duplicate delivery applies once** — the same command id delivered three times mutates state once.
2. **No ack → bounded retry → `Failed`** — a command that's never acked redelivers up to `MaxAttempts`, fails cleanly, and the next reconciliation cycle issues a fresh command for the still-open gap.
3. **Offline → reconnect** — a device that never ran a cycle reconciles to *current* desired state on its first connect, even if that state changed while it was "offline."
4. **A live fleet** — 40 simulated agents converge against one control plane concurrently, with a colored compliant/non-compliant board and a final `FleetHealth` snapshot.

## Full stack: Postgres, REST, and a real BitLocker policy

This swaps the in-memory control plane for a real ASP.NET Core API backed by PostgreSQL, adds
REST as the agent/control-plane transport, and wires up one real Windows policy end to end.

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

## Windows IPC: user-agent + hardened named pipe

This adds the user-context boundary: `Warden.Agent` can keep running as the service-side
remediator, while `Warden.UserAgent` runs in the logged-on user's desktop session and receives
notifications over a hardened named pipe. See [`WARDEN_COURSE_IPC.md`](WARDEN_COURSE_IPC.md) and
[`WARDEN_IPC.md`](WARDEN_IPC.md) for the full design write-up.

For the safe local demo, keep using fake BitLocker mode:

```powershell
$env:Agent__UseFakeBitLocker = "true"
$env:Agent__FakeBitLockerEnabled = "false"
$env:Agent__ControlPlaneBaseAddress = "http://localhost:5000"
dotnet run --project src/Warden.Agent
```

Demo flow:

1. Start the compose stack (above) and open `http://localhost:5000/dashboard`.
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

Running `dotnet run --project src/Warden.Agent` directly (as above) still demos the full
compliance loop -- reporting, drift detection, remediation -- but session launch fails gracefully
with a single logged line (`WTSQueryUserToken failed for session 1`, Win32 error 1314) instead of
a crash, and steps 4-7 simply won't happen. That failure mode is itself deliberate and covered by
`SessionAgentManagerTests`.

Security checks to show alongside the demo:

```powershell
dotnet test tests\Warden.Ipc.Tests\Warden.Ipc.Tests.csproj
dotnet test tests\Warden.Agent.Tests\Warden.Agent.Tests.csproj
```

The IPC tests prove ping/pong framing, service-pushed compliance messages, ACL creation, and
peer-session rejection. The agent tests prove per-session lifecycle, respawn after unexpected
user-agent exit, bounded backoff, and crash-loop circuit breaking.
