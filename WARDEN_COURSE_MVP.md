# WARDEN_COURSE_MVP.md — building `v0.2-mvp`

> **Target:** the MVP described in `WARDEN_MVP.md` — one real Windows policy (BitLocker), REST
> transport, PostgreSQL, and a bare dashboard, built on top of the `v0.1-core` reconciliation
> engine without changing it.
> **Format:** each session is **Learn → Build → Commit → Earn**. Every session ends with running,
> committed code. No session leaves the repo broken overnight.
> **Stack:** C#/.NET (current LTS, .NET 8+), ASP.NET Core, PostgreSQL, Docker Compose.
> **Precondition:** `v0.1-core` is tagged and green. This course does not modify `Warden.Core` —
> if a session seems to require a `Core` change, that's a signal the `v0.1-core` seam wasn't
> designed correctly and worth stopping to reconsider.

---

## How to run this course

- One session per sitting. Do them in order — each depends on the last.
- **Trigger phrase:** in the WARDEN project, say **"Start Warden MVP session N"** and I'll walk
  you through that session's Learn material, then pair with you on the Build, ending at the commit.
- The theme of this course is **swapping seams, not writing new correctness logic**. `v0.1-core`
  already proved the four hard behaviors. This course proves those seams (`ICommandStore`,
  `IDeviceRepository`, `IControlPlaneClient`) actually decouple the things they were designed to
  decouple.
- Don't let Session 3 (real Windows) balloon into a full policy engine. One rule, one command type.

## The finish line

By the end you have a tagged `v0.2-mvp`: the same reconciliation engine from `v0.1-core`, now
running against PostgreSQL, talking over REST, checking and remediating real BitLocker state on
a Windows machine, with a bare read-only dashboard, `docker compose up` working clean, and CI
green. The demo you can show: *turn BitLocker off on a real laptop → the system detects it within
one cycle → re-enables it automatically → dashboard flips red to green, no human involved.*

### Which session lands each MVP piece

| # | Piece | Lands in |
|---|---|---|
| 1 | PostgreSQL-backed stores | Session 1 |
| 2 | REST transport | Session 2 |
| 3 | Real BitLocker read | Session 3 |
| 4 | Real BitLocker remediation | Session 4 |
| 5 | Bare dashboard | Session 5 |
| 6 | Compose + CI + DESIGN.md + tag | Session 6 |

---

## Session 1 — PostgreSQL-backed stores

**Learn.** Why this is a pure infrastructure swap: `ICommandStore` and `IDeviceRepository` are
already the persistence boundary from `v0.1-core`. The risk here isn't correctness — it's
smuggling business logic into SQL that belongs in `Core`. Keep queries dumb; let `Reconciler` and
the guarded state-machine transitions stay exactly where they are.

**Build.**
- Add PostgreSQL to `docker-compose.yml`.
- Implement `PostgresCommandStore` and `PostgresDeviceRepository` against the same interfaces
  `Warden.Core` already defines.
- Schema: `devices`, `commands` (mirrors the `Command` record — id, device_id, action, status,
  attempts, timestamps), matching `CommandStatus` as an enum/text column.
- Re-run (or parallel-run) the existing `Core.Tests` failure-path suite against the Postgres
  implementation: duplicate acks, guarded transitions, one-in-flight-per-gap — all must still
  hold against a real database, not just in-memory.

**Commit.** `feat: postgres-backed command store and device repository`

**Earn.** The interfaces designed in `v0.1-core` absorbed a full persistence swap with zero
changes to `Warden.Core`. *Interview line:* "The store was always behind an interface — Postgres
just filled it in."

---

## Session 2 — REST transport

**Learn.** `IControlPlaneClient` exists for exactly this moment. What actually changes at the
transport layer: serialization, auth, HTTP-level timeouts and retries — distinct from the
domain-level command retry/redelivery logic you already built and proved in `v0.1-core`. Don't
let HTTP concerns leak into `Reconciler` or the command state machine.

**Build.**
- ASP.NET Core Web API host for `Warden.ControlPlane` exposing three endpoints:
  `POST /enroll`, `GET /devices/{id}/desired-state`, `POST /devices/{id}/report-state`.
- `RestControlPlaneClient : IControlPlaneClient` implementation the agent uses in place of the
  in-process client.
- Integration test: agent ↔ control plane over real HTTP (e.g. `WebApplicationFactory` or a
  live local host), re-proving the four hard behaviors hold end-to-end over the network, not
  just in-process.

**Commit.** `feat: REST transport for control plane client`

**Earn.** Transport swapped without touching `Core` or the reconciliation logic — proving the
seam was designed for exactly this. *Interview line:* "In-process now, REST next — the agent
never noticed the difference."

---

## Session 3 — Real BitLocker status (read-only)

**Learn.** `Win32_EncryptableVolume` via WMI, or shelling out to `manage-bde -status`, and why
this genuinely requires running as (or alongside) `LocalSystem` on a real Windows machine — the
one part of the MVP that can't be faked or run in a container. Keep this session read-only:
resist wiring remediation before you can reliably *observe* real state.

**Build.**
- Real `IActualStateProvider` (or equivalent seam) that queries BitLocker status instead of
  reading a simulated in-memory dict.
- `Warden.Agent` runs as a Windows Service (`BackgroundService`, `LocalSystem`) — same loop as
  `v0.1-core`, only the state source changed.
- Seed one `DesiredState` policy: `bitlocker.enabled = true`.
- Manual test: flip BitLocker off/on yourself, confirm the agent reports the correct actual state
  each cycle.

**Commit.** `feat: real BitLocker status check via Windows Service agent`

**Earn.** Same reconciliation loop, first real OS integration — the domain logic didn't care that
the state source went from a dict to WMI. *Interview line:* "The reconciler doesn't know or care
whether 'actual state' came from a test double or a real machine."

---

## Session 4 — Remediation (the self-heal, for real) ★ the MVP demo

**Learn.** The difference between reading state and mutating it. `manage-bde -on` needs elevation
and isn't instant — encryption itself takes time, so compliance won't flip green in exactly one
cycle. That's expected behavior, not a bug, and worth being able to explain clearly rather than
looking surprised by it in a demo.

**Build.**
- `enable-bitlocker` command wired through the *existing* command lifecycle
  (`Pending → Delivered → Acked`) — no new state machine, no new transport logic.
- Agent executes the command for real (`manage-bde -on C:`), then re-checks and reports.
- End-to-end manual test: turn BitLocker off on a real machine, watch it detected, remediated,
  and re-verified with no human step in between.

**Commit.** `feat: BitLocker remediation via existing command pipeline`

**Earn.** This is the actual MVP demo: red → fixed → green, no human touched it. Nothing about
the correctness machinery from `v0.1-core` had to change to get here. *Interview line:* "The
command pipeline built for a simulated setting handled a real, slow, elevated OS mutation without
modification."

---

## Session 5 — Bare dashboard

**Learn.** The dashboard is explicitly not graded on polish — its only job is to make red→green
*visible* for a screen-share, not to demonstrate frontend skill. Resist scope creep here.

**Build.**
- One read-only page (Razor page or a minimal API-backed static HTML table): device | rule |
  status | last check.
- No auth flow, no styling investment, no client-side framework.

**Commit.** `feat: bare compliance dashboard`

**Earn.** Visual proof of the loop at minimum time cost. *Interview line:* "I spent zero time on
CSS — the dashboard exists to make the loop visible, not to be a product."

---

## Session 6 — Capstone: Compose, CI, DESIGN.md, tag `v0.2-mvp`

**Learn.** What changed in the design doc's own claims. `v0.1-core`'s `DESIGN.md` predicted "at
1,000,000 devices, in-memory breaks first." Now that storage and transport are real, that section
needs to describe what actually happened, not just forecast it — and Session 4 likely taught you
something concrete about async/slow remediation that's worth writing down.

**Build.**
- `docker compose up` brings up the control plane + PostgreSQL cleanly from a fresh clone.
- CI (GitHub Actions) updated to run integration tests against a Postgres service container.
- `DESIGN.md` updated:
  - storage and transport sections move from "planned" to "implemented," with any surprises noted,
  - a note on BitLocker's async nature and what it implies about compliance-state timing,
  - revisit the 1,000,000-devices section with anything concrete learned from real Postgres/REST
    behavior, even if it's just "here's what I'd measure first."
- Final pass: confirm the documented run command works clean on a fresh machine.

**Commit.** `docs: DESIGN.md update + CI + docker compose`, then **`git tag v0.2-mvp`**.

**Earn.** The MVP's one-sentence demo from `WARDEN_MVP.md` is now literally true, not aspirational:
*"I turn BitLocker off on my laptop. Within ~60 seconds the system detects it. A few seconds later
the agent turns it back on — I never touched a thing."* *Interview line:* the whole repo, now
spanning simulated correctness proof through real Windows enforcement, walked through end to end.

---

## After `v0.2-mvp`

The natural next tag, per `WARDEN_PROJECT.md` and `README.md`:

- **`v0.3-ipc`** — the user-context agent + hardened named-pipe IPC across the System↔User
  privilege boundary. The highest-signal Windows piece for this role (Winlogon, System vs User
  context, IPC, privilege boundary). Say the word when you're ready and I'll draft
  `WARDEN_COURSE_IPC.md` in the same format.
- Beyond that: real-time push (SignalR), a second policy, then multi-tenancy — following
  `WARDEN_PROJECT.md`'s roadmap.

> A note on pacing: unlike `v0.1-core`, where every session landed new correctness logic, most of
> this course is **seam-proving** — showing that interfaces designed for one context (in-memory,
> in-process, simulated) hold up under a real one (Postgres, REST, actual Windows APIs). Sessions
> 1–2 are the lowest-risk and could be combined into one longer sitting if you'd rather; Session 3
> is the one that needs an actual Windows machine or VM, so line that up before you start rather
> than discovering the friction mid-session.
