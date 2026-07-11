# Warden IPC — the user-context agent, and why it has to exist

> **In one line:** `Warden.Agent` runs as `LocalSystem` and can already read and fix BitLocker —
> but a `LocalSystem` service cannot show a toast notification on your desktop. That one Windows
> constraint (Session 0 isolation) is the entire reason a second, per-session, user-context process
> has to exist and has to talk to the service over a boundary that's actually secured. `v0.3-ipc`
> builds that boundary.

---

## The problem (10-second version)

`v0.2-mvp` proved the reconciliation loop end-to-end: drift is detected, a command is issued,
`Warden.Agent` (running as `LocalSystem`) executes it, the dashboard flips green. That's the whole
loop *as long as everything the loop needs to do can be done by a `LocalSystem` service.*

It can't. Since Windows Vista, session 0 (where services run) is isolated from the interactive
desktop sessions (where users log in) specifically so a service **cannot** show UI on a user's
screen. `Warden.Agent` can silently re-enable BitLocker all day, but it cannot tell the user it
just did that — no toast, no notification, nothing. The moment the product needs *any* user-facing
signal (a "we just fixed something" toast, a self-service "sync now" button, per-user telemetry
like logon duration that only makes sense from inside the user's own session), it needs a second
process running **as that user**, and that process needs a channel back to the `LocalSystem`
service that can't be abused by anything else running on the box.

That channel — and making it actually secure, not just functional — is `v0.3-ipc`.

## How v0.3-ipc solves it

A small, per-session **user-context agent** (`Warden.UserAgent`) launches automatically whenever a
user logs in (including over RDP, including fast user switching — there can be more than one
active session on a box). It connects to the already-running `Warden.Agent` service over a
**named pipe** that only that specific session's authenticated user (and `LocalSystem` itself) can
open. The service uses that pipe to push user-facing signals down; nothing else on the machine —
not another user's session, not a lower-privileged process trying to reach `LocalSystem` — can talk
over it.

**The one capability we ship:** a desktop toast notification when BitLocker drifts and gets
remediated. It's the perfect first user-context feature because it's the smallest possible thing
that *only* a user-context process can do, which means the moment it works, the hardest part of the
whole subsystem (a correctly-scoped, ACL'd, peer-verified IPC channel) is already proven.

---

## What's in v0.3-ipc (and what's deliberately NOT)

| ✅ In v0.3-ipc | ❌ Not yet (fast-follow) |
|---|---|
| `Warden.UserAgent`: a per-session console/tray process | Full WinUI 3 companion app (self-service catalog, rich UI) |
| Named-pipe IPC, ACL'd to `LocalSystem` + the owning session's user | gRPC / localhost HTTP as an alternative transport |
| Peer verification (PID → session → SID checked against the pipe's expected owner) | Full mTLS-style mutual auth over the pipe |
| One user-context capability: a compliance-change toast notification | DEX telemetry (logon time, app crashes, resource pressure) |
| Multi-session correctness (launch per session, survive RDP / fast user switching) | Multi-tenancy, RBAC |
| Mutual liveness watchdog (service respawns a crashed user-agent, with backoff) | Full tamper-protection (anti-kill, signed binaries) |
| Existing command pipeline extended to route one action to the user-agent | A general-purpose "run anything in user context" channel |

**Rule of thumb:** if it isn't required to prove *"a `LocalSystem` service can safely and
correctly reach into a specific user's desktop session, and nothing else can,"* it's not in
`v0.3-ipc`.

---

## System design (v0.3-ipc)

```
┌────────────────────────── Windows laptop ──────────────────────────┐
│                                                                       │
│  Session 1 (interactive user)         Session 2 (RDP user, maybe)    │
│  ┌─────────────────────────┐          ┌─────────────────────────┐   │
│  │  Warden.UserAgent        │          │  Warden.UserAgent        │   │
│  │  (console/tray, USER)    │          │  (console/tray, USER)    │   │
│  │   • connects to \\.\pipe\│          │   • connects to \\.\pipe\│   │
│  │     WardenIpc-{session}  │          │     WardenIpc-{session}  │   │
│  │   • shows toast on       │          │   • shows toast on       │   │
│  │     "compliance changed" │          │     "compliance changed" │   │
│  └────────────┬─────────────┘          └────────────┬─────────────┘   │
│               │  named pipe, ACL'd to this session's user + SYSTEM   │
│               ▼                                       ▼               │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │  Warden.Agent  (Windows Service, LocalSystem)                  │  │
│  │   • existing reconciliation loop (unchanged from v0.2-mvp)     │  │
│  │   • SERVICE_CONTROL_SESSIONCHANGE → spawn/track a pipe server  │  │
│  │     per active session via CreateProcessAsUser                 │  │
│  │   • on a command that needs user context: write it to that     │  │
│  │     session's pipe instead of executing it directly            │  │
│  │   • watchdog: pipe dies unexpectedly → respawn with backoff    │  │
│  └───────────────────────────────┬───────────────────────────────┘  │
└──────────────────────────────────┼───────────────────────────────────┘
                                    │  HTTPS REST (unchanged from v0.2-mvp)
                                    ▼
                          Warden.ControlPlane.Api
```

**Three new moving parts:** the user-agent process, the named-pipe protocol between it and the
service, and the service's session-lifecycle bookkeeping (who's logged in, which pipe belongs to
which session). Everything below the REST line — the reconciliation loop, the control plane, the
database — is **unchanged**. That's deliberate: `v0.3-ipc` proves the IPC boundary in isolation,
the same way `v0.2-mvp` proved the storage and transport seams in isolation.

---

## Data flow — with a real example

### Step 0 — a session starts (logon, RDP connect, or fast-user-switch)

`Warden.Agent` (already running as `LocalSystem`) receives `SERVICE_CONTROL_SESSIONCHANGE` with
`WTS_SESSION_LOGON` for session `N`. It calls `WTSQueryUserToken(N)` to get that session's user
token, then `CreateProcessAsUser` to launch `Warden.UserAgent.exe` **inside session N, as that
user** — not as `LocalSystem`, and not as whatever account happens to be running the service.

### Step 1 — the user-agent connects

`Warden.UserAgent` opens `\\.\pipe\WardenIpc-{N}`. The service created that pipe with a
`PipeSecurity` ACL granting connect rights only to `LocalSystem` and the SID of the user who owns
session `N` — anyone else on the box (including a different logged-in user, or a non-elevated
process trying to guess the pipe name) gets `AccessDenied`.

### Step 2 — the service verifies the peer, not just the ACL

The ACL keeps strangers out at the OS level; the service double-checks anyway, because ACL
misconfiguration is exactly the kind of thing that causes a real local-privilege-escalation CVE.
On connect, it calls `GetNamedPipeClientProcessId`, resolves that PID's session ID, and confirms it
matches session `N` — the session this pipe was created for. A mismatch closes the connection and
logs it as a real security event, not a warning.

### Step 3 — a compliance event needs the user's attention

The reconciliation loop (unchanged since `v0.2-mvp`) detects `bitlocker.enabled` drift, issues the
remediation command, `BitLockerCommandExecutor` runs `manage-bde -on`, and the next report shows
compliant again. Now — new in `v0.3-ipc` — the worker also writes one message to session `N`'s
pipe:

```
{ "type": "ComplianceChanged", "rule": "bitlocker.enabled", "status": "Compliant" }
```

### Step 4 — the user sees it

`Warden.UserAgent` reads the message and raises a Windows toast: *"Warden re-enabled BitLocker on
this device."* The user finds out the same second the dashboard turns green — not from a helpdesk
ticket days later.

### Step 5 — the user-agent dies (crash, or someone kills it in Task Manager)

The service's pipe read fails. It waits a backoff interval, re-derives the user token for session
`N` (still logged on), and relaunches `Warden.UserAgent`. Repeated crashes inside the backoff
window trip a circuit breaker instead of respawning in a tight loop.

---

## Data model additions (on top of v0.2-mvp's)

No changes to `Warden.Core` — the domain model, the reconciler, and the command state machine are
untouched. What's new lives entirely in the agent-side projects:

```
SessionId          ( Windows session id, int )
PipeMessage         ( Type, Payload )   // small, versioned, e.g. ComplianceChanged, Ping/Pong
```

## Build order (each step ends in running, committed code)

1. **Unauthenticated pipe skeleton** — `Warden.UserAgent` console app connects to a named pipe
   `Warden.Agent` opens; round-trip a `Ping`/`Pong`. No ACLs, no session-awareness yet — prove the
   framing works before securing it. *Commit.*
2. **Harden the pipe** — `PipeSecurity` ACL restricted to `LocalSystem` + the intended user SID;
   peer verification via `GetNamedPipeClientProcessId` + session lookup; a rejected-connection test
   that proves an unauthorized SID actually gets refused, not just "should." *Commit.*
3. **Multi-session lifecycle** — `SERVICE_CONTROL_SESSIONCHANGE` handling, `CreateProcessAsUser` to
   launch `Warden.UserAgent` in the correct session, survive logon/logoff/RDP/fast-user-switch.
   *Commit.*
4. **Route one real command over the pipe** — the compliance-changed toast, wired through the
   *existing* command/report pipeline, not a side channel. *Commit.*
5. **Mutual watchdog** — service detects a dead user-agent and respawns it with backoff; a
   forced-crash test proves it recovers instead of looping. *Commit.*
6. **Capstone** — DESIGN.md updated with what the IPC boundary actually cost/proved, README demo
   script, tag `v0.3-ipc`.

Steps 1–2 are where the actual security work is; get those right before anything else.

## Definition of done (the demo you can show an interviewer)

> "I turn BitLocker off. The service detects it, turns it back on — same as the MVP. But now watch:
> a toast pops up on my desktop the moment it happens, pushed over a named pipe that only this
> `LocalSystem` service and my own logged-in session can talk over. Here — I'll kill the user-agent
> process in Task Manager. [~2 seconds later, a new one appears and reconnects.] And here's the ACL
> and the peer-PID check that stop anything else on this box from opening that pipe and pretending
> to be me."

That's the Windows-internals depth the posting's "BIG bonus" list is actually asking for — Session
0 isolation, `WTSQueryUserToken`, named-pipe ACLs, peer verification, and a self-healing IPC
boundary — proven live, not described in a paragraph.

---

## The very next step after v0.3-ipc (so you know where it's going)

Real-time push (SignalR) so the control plane can shove a command down immediately instead of
waiting for the next 60-second poll, a second policy beyond BitLocker (to prove the reconciler's
gap-to-command mapping generalizes), and then multi-tenancy — which is the point at which
`DeviceId` and friends need a `TenantId` threaded through, so it's worth having glanced at that
shape before this session, even though nothing here builds it yet.
