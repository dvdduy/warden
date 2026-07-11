# WARDEN_COURSE_IPC.md — building `v0.3-ipc`

> **Target:** the user-context agent + hardened named-pipe IPC described in `WARDEN_IPC.md` — the
> single highest-Windows-signal piece of this whole project, built on top of the unchanged
> `v0.2-mvp` reconciliation loop.
> **Format:** each session is **Learn → Build → Commit → Earn**. Every session ends with running,
> committed code. No session leaves the repo broken overnight.
> **Stack:** C#/.NET (current LTS, .NET 8+), `System.IO.Pipes`, Win32 interop (`WTSApi32`,
> `advapi32`) via P/Invoke, xUnit.
> **Precondition:** `v0.2-mvp` is tagged and green. This course does not modify `Warden.Core` — the
> reconciler, the command state machine, and the domain model stay exactly as they are. If a
> session seems to need a `Core` change, stop and reconsider; the IPC boundary is an agent-side
> concern end to end.

---

## How to run this course

- One session per sitting. Do them in order — session 2 (hardening) depends on session 1's
  skeleton existing to harden.
- **Trigger phrase:** in the Warden project, say **"Start Warden IPC session N"** and I'll walk you
  through that session's Learn material, then pair with you on the Build, ending at the commit.
- The theme of this course is **security work, not feature work**. Sessions 1–2 are the ones that
  actually matter for the "BIG bonus" list in the posting this project is built against — resist
  the urge to rush past the ACL/peer-verification session to get to something that demos more
  visibly. A toast notification is not the point; a toast notification that *only the right
  process can trigger* is the point.
- **This course needs a real Windows machine or VM**, unlike most of `v0.1-core`/`v0.2-mvp` which
  ran fine anywhere. Named pipes, session notifications, and `CreateProcessAsUser` are all
  Windows-only and mostly require at least one real interactive logon to test against. Line that up
  before starting Session 1.

## The finish line

By the end you have a tagged `v0.3-ipc`: the same `v0.2-mvp` control plane and reconciliation loop,
now with a per-session user-context agent that receives a compliance-change notification over a
named pipe that's ACL'd and peer-verified, survives logon/logoff/RDP/fast-user-switching, and
self-heals if killed. The demo you can show: *turn BitLocker off on a real laptop → the service
detects and remediates it exactly as in the MVP → a toast fires in the logged-in user's session the
same second → kill the user-agent process → it's back within seconds → then show the ACL and the
peer-PID check that stop anything else on the box from impersonating it.*

### Which session lands each piece

| # | Piece | Lands in |
|---|---|---|
| 1 | Unauthenticated named-pipe skeleton | Session 1 |
| 2 | ACL + peer verification (the actual security boundary) | Session 2 |
| 3 | Multi-session lifecycle (`SERVICE_CONTROL_SESSIONCHANGE`, `CreateProcessAsUser`) | Session 3 |
| 4 | One real command routed over the pipe (compliance-change toast) | Session 4 |
| 5 | Mutual watchdog + crash-loop protection | Session 5 |
| 6 | Capstone: DESIGN.md, README, tag `v0.3-ipc` | Session 6 |

---

## Session 1 — Unauthenticated named-pipe skeleton

**Learn.** `System.IO.Pipes.NamedPipeServerStream`/`NamedPipeClientStream` basics. Why a second
process has to exist at all: Session 0 isolation means a `LocalSystem` service cannot show UI on an
interactive desktop, full stop — this is a hard OS boundary, not a design choice. Framing a simple
message protocol over a pipe (length-prefixed JSON is enough; this isn't the session to build a
custom binary protocol).

**Build.**
- New project `Warden.UserAgent` — a console app that connects to a well-known pipe name and sends
  `Ping`, expecting `Pong` back.
- `Warden.Agent` opens a `NamedPipeServerStream` with **default, unrestricted** security for now —
  deliberately insecure, on purpose, so Session 2's hardening has something concrete to fix and you
  can *see* the difference in a test.
- A tiny shared `PipeMessage` protocol (type + payload), living in a new `Warden.Ipc` project (not
  `Warden.Core` — this is transport, not domain).
- Test: start the pipe server in-process, connect a client, assert `Ping` → `Pong` round-trips.

**Commit.** `feat: unauthenticated named-pipe skeleton between service and user agent`

**Earn.** The two processes can talk. Nothing is secure yet, and that's the point — you now have a
concrete, running thing to harden instead of designing security in the abstract. *Interview line:*
"I built the channel unsecured first, on purpose, so the ACL and peer-verification work in the next
session has something real to defend and a regression test that proves it."

---

## Session 2 — Harden the pipe (ACL + peer verification) ★ the session that matters most

**Learn.** The named-pipe ACL model (`PipeSecurity`, `PipeAccessRule`) and why the default is
dangerous here: an unrestricted pipe owned by a `LocalSystem` process is a classic local
privilege-escalation vector — any unprivileged process on the box could connect and pretend to be
the legitimate user-agent, or worse, trick the service into treating attacker input as a trusted
message from `LocalSystem`'s own channel. ACLs alone aren't enough to trust, though: `Windows
Integrity Levels` and impersonation subtleties mean you verify the actual connecting process too.
`GetNamedPipeClientProcessId` (P/Invoke) plus `ProcessIdToSessionId`/session lookup is how you
confirm "this connection really came from the session I created this pipe for," not just "someone
with an allowed SID."

**Build.**
- `PipeSecurity` on the server pipe: allow-list `LocalSystem` + the specific user SID this pipe was
  created for; explicitly deny `Everyone`/`Authenticated Users` as a defense-in-depth statement even
  though the allow-list alone would suffice.
- On connect, call `GetNamedPipeClientProcessId`, resolve the session id for that PID, and reject
  (close immediately, log as a security event) any connection whose session doesn't match the pipe's
  intended owner.
- Tests: a connection from an unauthorized SID is refused (simulate via a second local test account
  or a mocked ACL check — document whichever approach you use and why); a connection whose PID
  resolves to the wrong session is refused even if the ACL would have allowed the SID.

**Commit.** `feat: ACL and peer-verified named-pipe IPC`

**Earn.** This is the actual "hard-to-fake" Windows-security work — the piece that separates this
project from a toy IPC demo. *Interview line:* "The ACL keeps strangers off the pipe at the OS
level; the peer-PID-to-session check catches the case where the ACL is technically satisfied but
the connection still isn't who it claims to be — defense in depth, not just one control."

---

## Session 3 — Multi-session lifecycle

**Learn.** `SERVICE_CONTROL_SESSIONCHANGE` and `WTSRegisterSessionNotification` — how a service
finds out a user logged on, logged off, or switched via RDP/fast-user-switching. `WTSQueryUserToken`
+ `CreateProcessAsUser` — how a `LocalSystem` process launches a new process *as* a specific
interactive user in a specific session, correctly (environment block, working directory, and the
common pitfalls that make this API infamous to get right). Multi-session correctness: more than one
person can be logged into the same box at once (RDP), and each needs their own pipe and their own
user-agent instance.

**Build.**
- `Warden.Agent` overrides session-change handling (via `ServiceBase`'s
  `OnSessionChange`/`CanHandleSessionChangeEvent`, or the modern `Microsoft.Extensions.Hosting`
  equivalent) and maintains a `Dictionary<int SessionId, PipeServer>`.
- On `WTS_SESSION_LOGON`: derive the user token, `CreateProcessAsUser` to launch
  `Warden.UserAgent.exe` in that session, open that session's pipe.
- On `WTS_SESSION_LOGOFF`/disconnect: tear down that session's pipe server and any tracked process.
- Manual test (this one genuinely needs a real machine): log on as two different users via RDP +
  console simultaneously, confirm both get their own toast-capable user-agent.

**Commit.** `feat: per-session user-agent lifecycle via SERVICE_CONTROL_SESSIONCHANGE`

**Earn.** The service now correctly handles the case every real UEM product has to handle and every
toy demo skips: multiple concurrent sessions on one box. *Interview line:* "The naive version
assumes one user is ever logged in; RDP and fast-user-switching mean that's false on day one in any
real fleet, so the session bookkeeping is first-class, not an afterthought."

---

## Session 4 — Route one real command over the pipe

**Learn.** How this plugs into the *existing* command pipeline rather than becoming a parallel
system: the reconciliation loop already has a point where it knows compliance state just changed
(the same place `BitLockerCommandExecutor` reports success). That's where a user-context
notification gets triggered — as a side effect of the existing flow, not a new poller.

**Build.**
- `PipeMessage` gains a `ComplianceChanged { Rule, Status }` case.
- `ReportingAgentWorker` (or the point right after a command executes and re-reports), on detecting
  a status change for the current session's user, writes that message to the owning session's pipe.
- `Warden.UserAgent` reads it and raises a Windows toast (`Microsoft.Toolkit.Uwp.Notifications` or
  the raw `ToastNotificationManager` COM APIs — either is fine, the IPC boundary is the point, not
  toast polish).
- Test: a simulated compliance-change event produces exactly one `PipeMessage` on the correct
  session's pipe, none on others.

**Commit.** `feat: compliance-change notification routed over IPC to the user session`

**Earn.** The whole point of `v0.3-ipc` is now demonstrable end to end: something only a
user-context process can do, triggered safely from `LocalSystem`, over a channel proven secure in
Session 2. *Interview line:* "The notification isn't a new subsystem — it's the existing
report/remediate loop from the MVP, with one more side effect once the IPC boundary existed to carry
it."

---

## Session 5 — Mutual watchdog + crash-loop protection

**Learn.** Why "self-healing" has to include the IPC boundary itself, not just the reconciliation
loop: if `Warden.UserAgent` crashes (or is killed — deliberately, by malware or an annoyed user) and
nothing notices, the user silently stops getting compliance signals with no visible failure.
Exponential backoff + a circuit breaker matter here for the same reason they mattered in `v0.1-core`'s
ack-timeout sweeper: a naive "always respawn immediately" retry turns one crashing binary into a
CPU-pinning respawn loop.

**Build.**
- Service detects a dead pipe/process (failed read, or the tracked process handle signaling exit)
  and respawns `Warden.UserAgent` for that session, with backoff on repeated failures within a
  window.
- A repeated-crash test (kill the user-agent N times fast) trips a circuit breaker instead of
  spinning forever; a single crash after a healthy period respawns promptly.
- Manual test: kill `Warden.UserAgent` in Task Manager, confirm it's back within a few seconds and
  still receives the next compliance-change event correctly.

**Commit.** `feat: mutual watchdog and crash-loop protection for the user-agent`

**Earn.** The IPC boundary is now resilient the same way the command pipeline was proven resilient
in `v0.1-core` — bounded retry, not infinite hope. *Interview line:* "The user-agent is trusted to
crash — malware kills things, users kill things in Task Manager — so the service treats 'the
user-agent is gone' as an expected, handled state, not an exceptional one."

---

## Session 6 — Capstone: DESIGN.md, README, tag `v0.3-ipc`

**Learn.** What this phase's design doc needs that the last two didn't: real Windows-security
reasoning (the ACL + peer-verification trade-off), and an honest account of what was *not* hardened
(no code signing yet, no full tamper protection against a determined attacker with admin rights —
name the boundary of what "hardened IPC" means here versus a production security posture).

**Build.**
- `DESIGN.md`: a new section on the IPC security model — the ACL, the peer-verification check, what
  attack it stops and what it doesn't, and why named pipes over gRPC/localhost-HTTP for this specific
  boundary (lower attack surface, no network stack involved, Windows-native ACL model maps directly
  onto "which local principals can talk to this").
- `README.md`: the `v0.3-ipc` demo script (turn BitLocker off → remediate → toast → kill user-agent
  → respawn → show the rejected-connection test).
- Final pass: confirm the whole `v0.2-mvp` docker-compose stack still runs unmodified (this course
  shouldn't have touched it), and that the new agent-side projects build clean from a fresh clone.

**Commit.** `docs: DESIGN.md IPC security model + README demo script`, then **`git tag v0.3-ipc`**.

**Earn.** The single highest-signal Windows-internals piece of the whole project is done and
demoable, not just described. *Interview line:* the whole repo, now spanning simulated correctness
proof (`v0.1-core`) through real Windows enforcement (`v0.2-mvp`) through a hardened privilege
boundary (`v0.3-ipc`) — walked through end to end.

---

## After `v0.3-ipc`

Per `WARDEN_PROJECT.md`'s roadmap, the natural next phase is **production-ready**: real-time push
(SignalR) so commands don't wait for the next poll, a second policy beyond BitLocker (to prove the
reconciler's string-convention gap-to-command mapping actually generalizes — see the open question
in `DESIGN.md`), OpenTelemetry with correlation IDs that span the agent↔cloud boundary, and CI/CD
hardening. Multi-tenancy comes after that, and it's the point where `DeviceId` and the storage
layer need a `TenantId` threaded through everything — worth planning deliberately rather than
retrofitting once more features exist on top of the current single-tenant assumptions.

> A note on pacing: unlike `v0.2-mvp`, where most sessions were "prove this seam holds against a
> real backend," this course is where the actual novel engineering is — Sessions 1–3 build genuinely
> new capability (nothing in `v0.1-core`/`v0.2-mvp` touched Win32 session/security APIs at all).
> Don't compress them. Session 2 in particular is worth taking slowly; it's the one a security-minded
> interviewer will actually probe.
