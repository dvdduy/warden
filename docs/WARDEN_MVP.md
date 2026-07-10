# Warden MVP — the smallest thing that proves the idea

> **In one line:** every laptop runs an agent that pulls a cloud rule ("BitLocker must be ON"),
> checks itself, and reports compliant / non-compliant to a dashboard — and can fix itself.
> That's the whole MVP. Everything else is a fast-follow.

---

## The problem (10-second version)

Companies have thousands of Windows laptops that quietly drift out of a safe state — encryption gets turned off, a security setting changes — and **nobody knows until something goes wrong.** Today an IT person finds out from a support ticket, then fixes it by hand, one machine at a time.

## How the MVP solves it

Put a small agent on each laptop that, **every 60 seconds**, compares the machine against a rule defined in the cloud. If the laptop drifts, the dashboard turns red immediately — and the agent can turn the setting back on by itself. No ticket, no manual fix, no waiting.

**The one rule we ship:** *BitLocker (disk encryption) must be enabled.* It's the perfect first rule because checking and enabling it genuinely requires running as `LocalSystem` on Windows — so even the MVP shows real Windows systems-level work, not a toy.

---

## What's in the MVP (and what's deliberately NOT)

| ✅ In the MVP | ❌ Not yet (fast-follow) |
|---|---|
| 1 Windows agent (`LocalSystem` service) | User-context agent + hardened IPC |
| 1 rule: BitLocker must be ON | Many rules / policy editor |
| Enroll → pull → check → report loop | Real-time push (SignalR) |
| Auto-remediation (turn BitLocker on) | App deployment, smart groups |
| Control plane: 3 REST endpoints | Multi-tenancy, RBAC/SSO |
| PostgreSQL | Kafka, time-series telemetry |
| Bare-bones admin dashboard (1 table) | AI copilot, anomaly detection |
| Runs locally via Docker Compose | Kubernetes, multi-region |

**Rule of thumb:** if it isn't part of *"see drift → heal drift,"* it's not in the MVP.

---

## System design (MVP)

```
┌──────────────── Windows laptop ────────────────┐
│  Warden Agent  (Windows Service, LocalSystem)   │
│    • enroll once  → gets a deviceId + token     │
│    • every 60s:  pull rule → check → report     │
│    • can run: "enable BitLocker"                │
└───────────────────────┬─────────────────────────┘
                        │  HTTPS  (JSON REST)
                        ▼
┌──────────── Warden Control Plane ───────────────┐
│  ASP.NET Core Web API — 3 endpoints:            │
│    POST /enroll                                 │
│    GET  /devices/{id}/desired-state             │
│    POST /devices/{id}/report-state              │
│  + compliance is just: does actual == required? │
└───────────────────────┬─────────────────────────┘
                        │
                        ▼
                 PostgreSQL
        devices | policies | compliance_state
                        ▲
                        │ reads
                 Admin Dashboard
             (one table: device | rule | status)
```

**Three moving parts only:** the agent, one Web API, one database. The dashboard is a single read-only page. You can run the whole thing on your laptop with `docker compose up` (control plane + Postgres) and the agent as a console app in "service mode."

---

## Data flow — with real examples

### Step 0 — enroll (once, on install)

Agent → control plane:
```
POST /enroll
{ "hostname": "LAPTOP-DUY-01", "os": "Windows 11 23H2" }
```
Control plane → agent:
```
{ "deviceId": "dev_7Kq2", "token": "eyJhbGciOi..." }
```
Row written to `devices`: `dev_7Kq2 | LAPTOP-DUY-01 | enrolled`.

### Step 1 — pull the required state (every 60s)

Agent → control plane:
```
GET /devices/dev_7Kq2/desired-state      (Authorization: Bearer <token>)
```
Control plane → agent:
```
{
  "policies": [
    { "rule": "bitlocker.enabled", "operator": "equals", "value": true }
  ]
}
```
Plain English: *"BitLocker must be ON."*

### Step 2 — check the actual state (on the laptop)

Agent queries Windows (WMI `Win32_EncryptableVolume`, or `manage-bde -status`):
```
bitlocker.enabled  →  actual: false
```

### Step 3 — report

Agent → control plane:
```
POST /devices/dev_7Kq2/report-state
{
  "checks": [
    { "rule": "bitlocker.enabled", "actual": false, "compliant": false }
  ]
}
```
Row written to `compliance_state`: `dev_7Kq2 | bitlocker.enabled | NON_COMPLIANT | 2026-07-10T18:22Z`.

**Dashboard now shows:**

| Device | Rule | Status | Last check |
|---|---|---|---|
| LAPTOP-DUY-01 | BitLocker enabled | 🔴 Non-compliant | 18:22 |

### Step 4 — remediate (the "self-heal")

Because remediation is on, the next `desired-state` response includes a command:
```
{ "policies": [...], "commands": [ { "id": "cmd_A1", "action": "enable-bitlocker" } ] }
```
Agent runs it (`manage-bde -on C:`), then reports success. Next cycle:
```
{ "checks": [ { "rule": "bitlocker.enabled", "actual": true, "compliant": true } ] }
```

**Dashboard flips to green — no human touched it:**

| Device | Rule | Status | Last check |
|---|---|---|---|
| LAPTOP-DUY-01 | BitLocker enabled | 🟢 Compliant | 18:23 |

That green-after-red, with nobody in the loop, **is the demo.**

---

## Data model (3 tables)

```
devices           ( id, hostname, os, token_hash, enrolled_at )
policies          ( id, rule, operator, value, remediation_action )
compliance_state  ( device_id, rule, actual, compliant, checked_at )
```

## The 3 endpoints (all you build server-side)

| Method | Path | Does |
|---|---|---|
| `POST` | `/enroll` | create device, return id + token |
| `GET` | `/devices/{id}/desired-state` | return the rule (+ any remediation command) |
| `POST` | `/devices/{id}/report-state` | store actual vs required → compliant? |

---

## Build order (each step ends in running, committed code)

1. **Control plane skeleton** — ASP.NET Core Web API + PostgreSQL in Docker Compose. `POST /enroll` works; you can curl it and see a row in `devices`. *Commit.*
2. **Desired-state + report** — add the other two endpoints, seed one BitLocker policy, compute compliant = (actual == required). Test with curl. *Commit.*
3. **Dashboard** — one HTML/Razor page listing `compliance_state`. Red/green. *Commit.*
4. **The agent (read-only first)** — C# Windows Service (`BackgroundService`). Enroll, then the 60s loop: pull → **query real BitLocker status** → report. Watch the dashboard react to your actual machine. *Commit.*
5. **Remediation** — control plane emits `enable-bitlocker` when non-compliant; agent executes and re-checks. Watch red → green with no human. *Commit.*

Steps 1–3 are a weekend. Steps 4–5 are where the Windows work lives.

## Definition of done (the demo you can show an interviewer)

> "I turn BitLocker **off** on my laptop. Within ~60 seconds the dashboard goes **red**. A few seconds later the agent turns it back **on** and the dashboard goes **green** — I never touched a thing. Here's the `LocalSystem` service, the reconciliation loop, and the three-endpoint control plane behind it."

That single sentence proves the C#/.NET, the Windows systems-level work, the client-server design, and the self-healing story — the core of the Omnissa posting — in one live demo.

---

## The very next step after MVP (so you know where it's going)

Add the **user-context agent + hardened IPC** — a second process running in the logged-in user's session, talking to the `LocalSystem` service over an ACL'd named pipe. That's the single highest-signal piece for this role (Winlogon, System vs User context, IPC, privilege boundary), and the MVP is built so it slots in cleanly without a rewrite. After that: real-time push (SignalR), a second rule, then multi-tenancy.
