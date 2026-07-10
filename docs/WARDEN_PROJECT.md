# Warden — Autonomous Windows Endpoint Management & Security Control Plane

> A portfolio system reverse-engineered from the **Omnissa Staff Engineer** posting (Vancouver).
> Warden is a scoped, production-grade clone of the class of product Omnissa sells: a Windows
> endpoint agent + multi-tenant SaaS control plane that keeps a fleet of devices *configured,
> compliant, healed, and secure* — the "autonomous workspace" vision, built to be interview-defensible.

---

## Why this project (and not something else)

The posting is an unusual three-way hybrid. It simultaneously demands:

1. **Windows systems-level depth** — C#/.NET, Winlogon, User vs System context, Win32/COM, IPC, process isolation, multi-threading, hooking/interception, virtualization.
2. **Distributed SaaS engineering** — client-server, REST, "SaaS-based security," large-scale enterprise deployments, DevOps service ownership (CI/CD, logging, monitoring).
3. **The autonomous-workspace AI angle** — self-configuring, self-healing, self-securing.

Most portfolio projects hit one of these. A **UEM (Unified Endpoint Management) agent + control plane** is the single architecture where all three are structurally required, because it *is* a miniature of Omnissa's actual product (Workspace ONE UEM + Intelligent Hub + DEX + Security & Compliance). The interviewer does not have to imagine how your skills transfer — you built a smaller version of the thing they ship.

The load-bearing, hard-to-fake subsystem — and the one that maps directly onto the posting's "BIG bonus" list — is the **privilege boundary**: a `LocalSystem` Windows Service talking to a per-session user-context agent over hardened IPC, session-aware, tamper-resistant, self-healing. That alone demonstrates Windows internals maturity that a CRUD app never will.

---

## 1. Product Vision

Warden is an **autonomous endpoint control plane** for Windows fleets. Every managed device runs a lightweight agent that continuously reconciles the machine toward a cloud-defined desired state — config, security posture, installed apps, compliance — and streams health/DEX telemetry back. The control plane evaluates that telemetry, detects drift and degradation, and heals devices automatically (or with a human confirmation gate for destructive actions). The north star: **a device should self-configure on enrollment, self-heal when it drifts, and self-secure when its posture regresses — without a helpdesk ticket.**

## 2. What problem does it solve?

Enterprises manage tens of thousands to millions of Windows endpoints that are offline half the time, roam across networks, run different agent versions, and drift out of compliance constantly. Today that means reactive helpdesk tickets, manual remediation, inconsistent security posture, and slow, risky fleet-wide changes. Warden replaces "someone notices and files a ticket" with **continuous reconciliation + proactive, telemetry-driven self-healing**, while giving admins safe, staged control over the whole fleet.

## 3. Who are the users?

- **End users** (employees): experience zero-touch enrollment, a self-service app catalog, transparent compliance status, and "fix-it" remediation — via the WinUI companion app.
- **IT admins / desktop engineers**: define policies, assign them to smart groups, push apps, run commands, and watch compliance dashboards.
- **Security & compliance teams**: define posture baselines, audit device state, prove compliance, and get alerted on regressions.
- **SRE / platform team (you)**: own the SaaS control plane end-to-end (the DevOps-ownership signal in the posting).

## 4. Why would a company invest in building it?

Endpoint management is a multi-billion-dollar category (Intune, Workspace ONE, Jamf, CrowdStrike, Tanium) precisely because it converts labor-intensive, error-prone manual IT into scalable automation, and because it is the enforcement point for security and compliance. It reduces helpdesk cost, shrinks the attack surface, accelerates safe change (rings/canaries), and produces the audit evidence regulated industries require. For Omnissa specifically, it *is* the flagship revenue product.

---

## Functional Requirements

### 6. Core MVP features
- **Zero-touch-ish enrollment**: agent installs, enrolls into a tenant, receives a device identity (client cert), reports inventory.
- **Desired-state reconciliation loop**: agent pulls assigned policies, diffs against actual machine state, applies changes, reports back — idempotently.
- **One real compliance policy end-to-end** (e.g., BitLocker/disk-encryption enabled, or password/PIN policy): evaluate → mark compliant/non-compliant → surface in console.
- **Command channel**: admin issues a command (e.g., "sync now," "collect logs"), device acknowledges and executes.
- **WinUI companion app**: enrollment status, compliance state, self-service "sync"/"fix" actions.
- **Admin console + API**: device inventory, policy assignment, compliance view.

### 7. Advanced features
- **Real-time command delivery** over a persistent channel (SignalR/WebSocket) with long-poll fallback for restricted networks.
- **App deployment**: package (MSI/EXE/winget), assign, install, verify, report — with install-order and dependency handling.
- **Remediation engine ("self-heal")**: detected non-compliance → automated corrective action (e.g., re-enable BitLocker, restart a stopped critical service), with backoff and loop protection.
- **Smart groups**: dynamic device assignment by attributes (OS build, org unit, tag, posture).
- **Phased agent auto-update**: staged rollout (canary → early → broad), health-gated, auto-rollback.
- **DEX telemetry**: boot time, logon duration, app crashes/hangs, resource pressure, user-context app usage.

### 8. Enterprise features
- **Multi-tenancy** with hard data isolation and per-tenant encryption keys.
- **RBAC + SSO** for admins (OIDC), scoped roles (read-only auditor, operator, admin).
- **Immutable audit log** of every admin/device/AI action.
- **Compliance reporting** (exportable posture evidence per policy/framework).
- **Multi-region control plane**, tenant data residency, DR with defined RPO/RTO.
- **Kill switch + fleet-wide rollback** for a bad policy or bad agent build.

### 9. AI capabilities
- **Anomaly detection** on device telemetry (multivariate: an autoencoder or statistical baseline flags degrading devices *before* the user complains).
- **Ops copilot / remediation agent**: given a non-compliant or anomalous device's telemetry + event logs, diagnose likely root cause and propose/execute a remediation — **read-only by default, confirmation gate for destructive actions**.
- **Natural-language fleet queries**: "show Finance-group devices with encryption disabled" → structured query.

---

## Non-functional Requirements

### 11. Scalability
- Target design point: **1M+ devices, 10k+ tenants**. Devices are mostly-offline, bursty (Monday 9am thundering herd), and heterogeneous by agent version.
- Horizontal scale on the ingest and command paths; partition telemetry by tenant/device. **Jittered check-in** to avoid synchronized load.

### 12. Reliability
- **Fail safe, not fail destructive**: if the control plane is unreachable, agents keep their last-known-good policy and never execute stale destructive commands blindly.
- At-least-once command delivery + **idempotency keys** + acks + dedup (exactly-once is not attempted; the design demonstrates *why*).
- Mutual watchdogs (service ↔ user-agent) with exponential backoff and circuit breakers to prevent crash loops.

### 13. Security
- **mTLS device identity** (per-device client cert issued at enrollment; rotation supported).
- Secrets on-device in **DPAPI / Windows Credential Manager / TPM-backed** storage, never plaintext.
- **Hardened IPC** across the SYSTEM↔User boundary (named-pipe ACLs + peer verification, or authenticated localhost gRPC) to prevent local privilege escalation.
- **Tamper resistance**: protect the service from being killed by malware/user; auto-restart; signed binaries and signed policy payloads.
- Tenant-scoped authz on every API call; per-tenant KMS keys; least-privilege service accounts.

### 14. Performance
- Agent footprint budget (e.g., **<1% avg CPU, <100 MB working set**) — it runs on *every* endpoint, so a leak is a fleet-wide incident. Consider self-contained/NativeAOT trade-offs.
- Control-plane p99 latencies defined for enrollment, check-in, command dispatch, telemetry ingest.

### 15. Observability
- **OpenTelemetry** end-to-end: structured logs, metrics, traces with **correlation IDs that span agent → cloud** (you must debug machines you can't touch).
- Remote log/crash-dump collection with symbolication; per-fleet health SLO dashboards; alerting on rollout health regressions.

### 16. Multi-tenancy
- Isolation model (recommend **shared schema + tenant_id + row-level security**, with the isolation boundary tested); per-tenant rate limits (noisy-neighbor protection); per-tenant keys and audit.

### 17. Cost optimization
- **Edge aggregation** (agent summarizes/samples telemetry) vs raw shipping — bandwidth/storage vs fidelity trade-off.
- Tiered telemetry storage (hot time-series → cold object storage), autoscaling ingest, right-sizing the persistent-connection fleet vs polling.

---

## System Design

### 19. High-level architecture

```
        ┌────────────────────────── Windows Endpoint ──────────────────────────┐
        │  WinUI 3 Companion App (user)                                          │
        │        │  (localhost, authenticated)                                   │
        │  Warden User Agent  ── hardened IPC (named pipe / gRPC) ──┐            │
        │   (per-session, USER context; DEX + UI + notifications)   │            │
        │                                                           ▼            │
        │  Warden Service (LocalSystem)                                          │
        │   • reconciliation loop  • policy enforcement (Win32/COM/WMI/ETW)      │
        │   • telemetry collector  • command executor  • watchdog/self-heal      │
        └───────────────┬───────────────────────────────────────────────────────┘
                        │ mTLS: REST check-in + SignalR command channel
                        ▼
        ┌──────────────────────── Control Plane (SaaS, K8s) ────────────────────┐
        │  Enrollment/Identity │ Device Mgmt │ Policy │ Command&Control          │
        │  Compliance Engine   │ Remediation/Automation │ Telemetry Ingestion    │
        │  Admin API + Console │ AI Service (anomaly + copilot)                   │
        └───────┬─────────────┬───────────────┬───────────────┬─────────────────┘
                │             │               │               │
          PostgreSQL     TimescaleDB       Redis           Kafka            Blob (S3/Azure)
        (inventory,     (device/DEX       (cache, locks,  (telemetry &     (packages, agent
         policy, audit)  telemetry)        presence,      command events)   binaries, logs,
                                           pub/sub)                          crash dumps)
```

**Architectural recommendation:** start the control plane as a **modular monolith** (one ASP.NET Core deployable with clean module boundaries), and extract the two paths that actually need independent scaling first — **telemetry ingestion** and the **command channel**. Splitting into 9 microservices on day one is the wrong staff-level call and you should be able to say why.

### 20. Services
- **Enrollment/Identity**: enrollment tokens, device cert issuance/rotation, tenant binding, admin OIDC.
- **Device Management**: inventory, desired-state store, check-in endpoint.
- **Policy/Profile**: policy definitions, assignment (smart groups), versioning, signing.
- **Command & Control**: durable per-device command queue, dispatch, ack tracking, idempotency.
- **Compliance Engine**: evaluate telemetry vs policy → compliance state → events.
- **Remediation/Automation**: rules mapping detected issues → actions; confirmation gating; loop protection.
- **Telemetry Ingestion**: high-throughput ingest → Kafka → stream processing → TimescaleDB/blob.
- **AI Service**: anomaly models + copilot agent.
- **Admin API + Console**: React console (kept light; role is backend/systems).

### 21. APIs
- **Device-facing** (mTLS): `POST /enroll`, `GET /desired-state` (with ETag/version for cheap no-op check-ins), `POST /report-state`, `POST /telemetry`, `POST /commands/{id}/ack`, SignalR hub `/hub/commands`.
- **Admin-facing** (OIDC + RBAC): CRUD for policies, assignments, apps, devices; `POST /devices/{id}/commands`; compliance/report queries; audit query.
- **Versioning + capability negotiation**: agents advertise a capability set; the control plane serves N agent versions simultaneously (the fleet never updates atomically).

### 22. Data flow (happy path)
1. Agent enrolls → gets cert + tenant + initial desired state.
2. Reconciliation loop: `GET /desired-state` (ETag) → diff vs actual → apply via Win32/WMI/registry → `POST /report-state`.
3. Telemetry stream: agent samples/aggregates → `POST /telemetry` → Kafka → stream processor → TimescaleDB (+ blob for large payloads).
4. Compliance engine consumes telemetry/state events → recomputes posture → emits compliance events.
5. Remediation engine consumes compliance events → enqueues remediation command (auto or gated) → Command & Control → SignalR push (or next check-in) → agent executes → acks.

### 23. Databases
- **PostgreSQL** — relational source of truth: tenants, devices, users, policies, assignments, compliance state, command history, audit. (SQL Server is the idiomatic .NET choice; PostgreSQL is recommended for cloud portability — be ready to defend the choice.)
- **TimescaleDB** — device metrics/DEX time-series (ClickHouse is the scale-out option at very high cardinality).
- **Redis** — cache (desired-state, policy), distributed locks, **device presence** (who's online), rate limiting, pub/sub for live console updates.
- **Object storage (S3/Azure Blob)** — app packages, signed agent binaries, log bundles, crash dumps, large telemetry.

### 24. Messaging
- **Kafka** as the ingestion/event backbone (telemetry + compliance + command events), partitioned by tenant/device. Note NATS/Redis Streams as a lighter alternative and Azure Service Bus as the idiomatic managed .NET option — the trade-off (ops burden vs control vs throughput) is a talking point.

### 25. Caching
- Redis for hot desired-state and policy lookups; ETag-based conditional check-ins so an unchanged device does near-zero work; short-TTL presence keys.

### 26. Deployment
- Control plane: Docker → Kubernetes (AKS/EKS) → Helm; **canary/blue-green** with automated health gates; multi-region.
- Agent: **code-signed MSI**, versioned, distributed via blob/CDN; **ring-based rollout** (canary→early→broad) with health gating and rollback.
- **CI/CD**: GitHub Actions — build, unit + integration tests (with mocking/instrumentation), sign, package, deploy; separate agent and control-plane pipelines.

### 27. Cloud services
- Kubernetes, managed Postgres, managed Kafka (or Service Bus), object storage, KMS (per-tenant keys), CDN for agent/app distribution, secrets manager, OTel collector + a metrics/traces backend (Grafana/Prometheus/Tempo or a managed APM).

---

## AI Components

### 29. LLM usage
The **Ops Copilot**: root-cause diagnosis and remediation suggestion from a device's telemetry + event logs + compliance history. **Recommended framework: Microsoft Semantic Kernel** (native .NET agent/orchestration) — a much stronger signal than a Python stack for *this* shop, and it proves you can do agentic AI in the language they hire for. (Note LangGraph/Python as the alternative you know.)

### 30. RAG
Retrieve over: remediation runbooks, KB articles, past incident resolutions, policy documentation, and the device's own recent history. Grounds diagnoses in your org's actual playbooks rather than generic advice.

### 31. Agents
A diagnosis→plan→act agent: read telemetry, correlate against known failure signatures, propose a remediation *plan*, and — only after a confirmation gate for anything mutating — execute via the same command/remediation pipeline a human would use (never a privileged side channel).

### 32. Workflow orchestration
Semantic Kernel plan with tool functions: `getDeviceTelemetry`, `getEventLogs`, `searchRunbooks`, `proposeRemediation`, `executeCommand(requiresConfirmation=true)`. Deterministic guardrail layer wraps every mutating tool.

### 33. Evaluation
An eval set of labeled incidents (telemetry snapshot → known-good remediation). Metrics: **diagnosis accuracy, remediation success rate, false-positive remediation rate, time-to-resolution**. Regression-gate the copilot in CI like any other component.

### 34. Guardrails
Read-only by default; explicit **confirmation gate** for destructive actions (wipe, uninstall, disable); scoped permissions; dry-run mode; every AI-initiated action is audited and rate-limited; hard allowlist of executable remediations. (This mirrors the guardrail philosophy you already applied to the Forge ops agent — the pattern transfers cleanly.)

### 35. Memory
Per-device incident history and resolution outcomes feed back as retrieval context, so repeated issues resolve faster and the copilot "learns" your fleet's patterns without model retraining.

### 36. Prompt engineering
Structured tool-use prompts; telemetry summarized (not dumped) to fit context; few-shot examples of good root-cause writeups; strict output schema for proposed actions so the guardrail layer can parse and validate them.

---

## Engineering Challenges

### 38. Difficult technical problems the project should solve
- **SYSTEM↔User IPC security**: a hardened channel between a LocalSystem service and a user-context agent that cannot be abused for local privilege escalation.
- **Multi-session correctness**: right user-context behavior per session under RDP / fast user switching (`WTSRegisterSessionNotification`, `SERVICE_CONTROL_SESSIONCHANGE`, Winlogon context).
- **Reconciliation under unreliable connectivity**: offline/roaming/NAT'd devices; idempotent, eventually-consistent convergence.
- **Command delivery guarantees**: at-least-once + idempotency + acks + dedup; the impossibility of exactly-once, handled honestly.
- **Self-update without bricking**: update a running agent, health-gate it, roll back — the "replace the engine mid-flight" problem.
- **Telemetry at scale**: backpressure, sampling, edge aggregation, high-cardinality time-series.
- **N-version compatibility**: control plane serves many agent versions at once; capability negotiation, forward/backward-compatible payloads.
- **Tamper resistance + self-healing** without infinite crash loops (backoff, circuit breakers).
- **Debugging machines you can't access**: correlation IDs across the agent↔cloud boundary, remote crash-dump collection + symbolication.

### 39. Trade-offs the design should demonstrate
- Persistent connection (SignalR — real-time, but scale/resource cost) **vs** polling (simple, cheap, higher latency).
- Push **vs** pull for desired state.
- At-least-once + idempotency **vs** chasing exactly-once.
- Edge aggregation **vs** raw telemetry (cost/bandwidth vs fidelity).
- Modular monolith **vs** microservices (and *when* to extract).
- Kafka **vs** managed bus (ops burden vs control).
- C#/.NET agent (velocity, the posting's requirement) **vs** C++ (footprint/control) — including the .NET-on-endpoint footprint discussion and NativeAOT/self-contained mitigation (note the posting caps at .NET 6).
- Shared-schema multi-tenancy **vs** schema/DB-per-tenant (density vs isolation).

### 40. Production scenarios it should handle
- A bad policy pushed fleet-wide → **staged rollout + kill switch + rollback** save you.
- Control-plane outage → agents fail *safe* (keep last-known-good, no destructive stale commands).
- Monday-9am thundering herd (1M check-ins) → jitter + backpressure.
- A device offline for 3 months returns → reconcile safely, don't replay stale destructive commands.
- Malware tries to kill the agent → tamper protection + auto-restart.
- Tenant A data appearing for Tenant B → isolation tests that *fail loudly*.
- Agent memory leak → canary + health-gated rollout stops it before broad.

---

## 41. Resume Value

This project is a legible, scoped clone of Omnissa's flagship product, and it hits **every** line of the posting:

- **C#/.NET across the stack** (agent + ASP.NET Core control plane; Framework-and-Core coexistence).
- **Windows internals / systems-level**: LocalSystem service, Winlogon/session awareness, User-vs-System context, Win32/COM/WMI/ETW, IPC, process isolation, tamper protection, DPAPI/TPM — i.e. the "BIG bonus" list, actually built.
- **WinUI + client-server + REST** companion app and APIs.
- **Distributed SaaS + large-scale enterprise deployment**: multi-tenancy, fleet-scale telemetry, rings/canaries.
- **DevOps service ownership**: CI/CD, OTel logging/metrics/tracing, SLOs, on-call runbooks.
- **SaaS-based security**: mTLS device identity, signed policies, per-tenant keys, compliance enforcement.
- **The AI/autonomous-workspace narrative**, done responsibly (guardrailed, evaluated).

The one-sentence interview hook: *"I built a small Workspace ONE — a LocalSystem Windows agent that reconciles endpoints against a multi-tenant SaaS control plane, self-heals from telemetry, and gates a remediation copilot behind confirmation."* That sentence is the job.

## 42. Interview Readiness

**System design**
- Design a UEM/MDM system for 1M mostly-offline Windows devices.
- Design command delivery to offline-capable devices with delivery guarantees.
- Design multi-tenant, high-cardinality telemetry ingestion.
- Design safe fleet-wide config rollout (rings, kill switch, rollback).
- Design agent self-update that can't brick the fleet.

**Coding**
- Thread-safe named-pipe/gRPC IPC server with peer authorization.
- Idempotent command handler with dedup by idempotency key.
- Reconciliation diff engine (desired vs actual → minimal action set).
- Retry with exponential backoff + jitter + circuit breaker.
- Parse ETW / event-log stream into structured telemetry.

**Windows internals**
- System vs User context; why some actions need a user-session agent.
- Winlogon, session notifications, multi-session correctness.
- Securing a service against being killed; auto-restart design.
- DPAPI vs Credential Manager vs TPM for on-device secrets.
- Named-pipe ACLs and preventing local privilege escalation.

**Cloud / DevOps**
- Multi-region SaaS with tenant data residency; DR (RPO/RTO).
- K8s canary/blue-green with automated health gates.
- Correlation-ID tracing across agent↔cloud; debugging inaccessible machines.

**AI**
- Guardrailing an agent that can execute destructive actions.
- Evaluating a remediation copilot; what metrics, what regression gates.
- RAG grounding on runbooks; preventing hallucinated remediations.
- Anomaly detection design (autoencoder vs statistical baselines) and false-positive cost.

**Behavioral**
- A time you owned a service end-to-end in DevOps mode.
- A bad rollout and how you contained it.
- Debugging a fleet-wide issue you couldn't reproduce locally.
- A hard trade-off you made and the one you'd revisit.

---

## Roadmap

### 44. MVP (2–4 weeks)
Single-tenant. Windows Service + user agent + **named-pipe IPC** + WinUI enrollment. ASP.NET Core control plane: enrollment, **one policy type** (BitLocker or password compliance), desired-state check-in over REST, compliance evaluation, PostgreSQL. Basic admin API/console. Dockerized, runs locally. **Every session ends with running, committed code** (matches your Learn→Build→Commit→Earn loop).

### 45. Production-ready version
mTLS device identity, SignalR command channel (+ long-poll fallback), Kafka telemetry ingestion, remediation engine with **one real self-heal**, ring-based agent auto-update, OpenTelemetry + correlation IDs, CI/CD, integration tests with mocking/instrumentation.

### 46. Enterprise version
Multi-tenancy (isolation + per-tenant keys + rate limits), RBAC/SSO (OIDC), immutable audit log, smart groups, app deployment, multi-region + DR, SLOs, compliance reporting, kill switch + fleet rollback.

### 47. Staff Engineer version
Capability negotiation / N-version compatibility, automated rollback on health regression, chaos/fault injection, a documented cost model + optimizations, formal reconciliation semantics, tamper protection, the **guardrailed + evaluated AI copilot**, and the *leadership artifacts* that actually signal "staff": a design doc, ADRs, an on-call runbook, and an SLO dashboard. At this level the writing and the trade-off reasoning matter as much as the code.

## 48. Stretch Goals (make it stand out)
- **Fleet simulator / digital twin**: spin up thousands of virtual agents to test a policy against a simulated fleet *before* real rollout (huge interview differentiator).
- **Cross-platform agent** via shared .NET core (macOS/Linux) with platform-specific enforcement adapters.
- **TPM-backed device attestation** for hardware-rooted identity.
- **NativeAOT agent** for a tiny footprint (and the .NET-version trade-off discussion).
- **Policy DSL / WASM policy sandbox** for safe, sandboxed custom policies.
- **Offline-first conflict resolution** when a long-offline device returns with local changes.

---

### Notes on reuse from your existing tracks
- The **reconciliation-loop** pattern is the same shape as Forge's Kubernetes operator — different domain (endpoint vs cluster), transferable mental model and talking points.
- The **guardrailed ops agent** (read-only default + confirmation gate + audit) ports directly from Forge; here it runs in Semantic Kernel/.NET instead of LangGraph, which *adds* range rather than repeating yourself.
- The **autoencoder anomaly detection** from Mosaic maps cleanly onto device-telemetry anomaly detection.

This is a distinct *third* target (Omnissa) alongside Forge (AMD) and Mosaic (Workday). If you want, I can turn this blueprint into a session-by-session curriculum in the same Learn→Build→Commit→Earn format with its own trigger phrase — say the word and I'll draft `WARDEN_COURSE.md`.
