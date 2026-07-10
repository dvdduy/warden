# Take-home exercise: device fleet reconciliation service

> **What this is:** a self-contained engineering exercise written the way a hiring team would hand it
> to you. It is also the `v0.1-core` build target for Warden — the reusable heart the MVP and every
> extension sit on top of. Build against it as if it were assigned.
>
> **Time budget:** ~6–8 hours of focused work. A small thing done well beats a big thing half-finished.
> **Stack:** C#/.NET preferred (it's the target role's stack). Use another only if you can defend it.

---

## Context

You're building the core of a system that keeps a fleet of machines in a known-good state. A central **control plane** stores what each device *should* look like (its **desired state**). Each device runs an **agent** that reports what it *actually* looks like and carries out instructions. When the two disagree, the control plane issues a **command** to close the gap; the agent applies it and reports back.

Real fleets are unreliable: devices go offline for hours, networks drop mid-request, and the same message can arrive twice. **The exercise is about staying correct anyway** — not about how many features you can add.

## What you'll build

Two components and the protocol between them:

1. **Control plane** — a service that:
   - registers a device and issues it an identity,
   - serves a device its desired state on request,
   - accepts a device's reported actual state,
   - decides when actual ≠ desired and issues a command to reconcile,
   - tracks each command's lifecycle (pending → delivered → acknowledged).

2. **Agent (simulated)** — a process (or N concurrent processes) that:
   - registers, then loops: fetch desired state → compare to a local "actual" state → report → apply any command → acknowledge.
   - You **simulate** the device. "Actual state" is just an in-memory value the agent mutates when it applies a command. No real OS calls needed — a command like `set:featureX=on` flipping a variable is completely fine.

Keep the domain trivial on purpose. A "device" can have one setting (e.g. `featureX: on|off`); desired state is `on`; a non-compliant device gets a `set featureX=on` command. **The domain is not the point — the delivery guarantees are.**

---

## The requirements that actually matter

Everything above is scaffolding. **This section is the exercise.** Your submission is judged mostly on whether these four behaviors are correct:

1. **At-least-once delivery with idempotency.** A command may be delivered more than once. Applying the *same* command twice must have the *same* effect as applying it once. (Design implication: commands need stable IDs and the agent needs to recognize one it's already applied.)

2. **Offline → reconnect.** A device can disappear mid-cycle and come back minutes later. On return it must reconcile correctly — apply what it still needs, and **not** blindly replay stale or superseded commands.

3. **Duplicate delivery / duplicate acks.** The network can duplicate both the command going out and the acknowledgment coming back. Neither should corrupt state or leave a command stuck.

4. **The device that never acknowledges.** A command sent but never acked must not hang forever. Define and implement the behavior — redelivery after a timeout, a bounded retry count, a terminal "failed" state — and be able to justify it.

If you only have time for part of the exercise, **spend it here.** A submission that nails these four for one setting beats one that manages fifty settings but double-applies commands.

## Core functional requirements

- A device can register and is uniquely identified thereafter.
- The control plane computes compliance (`actual == desired`) and issues at most one in-flight reconciling command per gap.
- Command state is observable — you (or a test) can see whether a command is pending, delivered, acked, or failed.
- The agent loop is idempotent end-to-end: running it repeatedly on an already-compliant device does nothing and issues no new commands.

> **Deliberately unspecified:** the transport (REST? polling? a queue?), the storage (in-memory? SQLite? Postgres?), and the exact API shape. **Designing these is part of what's being evaluated.** Pick, and justify in `DESIGN.md`.

---

## Deliverables

1. **Running code.** `docker compose up` (or an equally trivial documented command) starts the control plane; a documented command runs the agent(s). It must run on the reviewer's machine without hand-holding.
2. **Tests focused on the hard paths.** Concurrency and failure: duplicate delivery, redelivery after no-ack, offline/reconnect, idempotent re-application. **Do not** pad coverage with getter/setter tests — we're looking at *what* you test, not the percentage.
3. **`DESIGN.md`** (~1 page). Covers:
   - your delivery-guarantee approach and why (why at-least-once + idempotency, not an attempt at exactly-once),
   - how you handle each of the four hard behaviors above,
   - the transport/storage trade-offs you chose,
   - **what changes at 1,000,000 devices** (what breaks first, what you'd redesign),
   - **what you cut** for time and what you'd do next.

## Constraints & scope

- **~6–8 hours.** We mean it. If you're gold-plating, stop and write down what you'd have done instead.
- **No UI.** Don't spend a minute on CSS or a dashboard — it isn't graded and it's a trap.
- **Simulate the device.** No real OS/hardware calls required.
- **Small is good.** One setting, one command type, done correctly and completely.

---

## How this is evaluated (the rubric)

Weighted, highest first — this is what the reviewer actually scores:

| Weight | Criterion | What "strong" looks like |
|---|---|---|
| ★★★★★ | **Correctness under failure** | The four hard behaviors provably work; idempotency is real, not hoped-for |
| ★★★★ | **Design reasoning (`DESIGN.md`)** | Clear trade-offs, honest about limits, credible 1M-device answer |
| ★★★ | **Code quality** | Readable, the hard logic is isolated and testable, sensible boundaries |
| ★★★ | **Tests target the right things** | Failure/concurrency paths covered; not coverage theater |
| ★★ | **Production-mindedness** | Structured logging, a health check, graceful failure handling |
| ★★ | **Communication** | README a human can follow; commit history tells a coherent story |

### What we will *not* penalize
- No UI (we asked you to skip it).
- Unfinished features **that you flagged** in `DESIGN.md`.
- A stack choice you defended, even if it's not our default.
- In-memory storage instead of a database, *if* you explain the trade-off.

### What sinks a submission
- Commands that double-apply, or a no-ack command that hangs forever.
- Tests that only cover the happy path.
- A README that's just "clone and run" with zero reasoning anywhere.
- One giant "final" commit with no history.
- Code you clearly can't explain — because the follow-up interview is us picking a file and asking you to walk through a decision and then extend it live.

---

## Optional stretch (only if you're under budget and curious)

Pick **at most one**, and only after the core is solid and tested. Note in `DESIGN.md` that it's a stretch.

- **Concurrent fleet:** run 100+ simulated agents at once and show delivery stays correct under contention.
- **Persistence & restart:** the control plane survives a restart with no lost or duplicated commands (recovery is the interesting part).
- **Per-tenant isolation:** two tenants' fleets that can't see or affect each other, with a per-tenant command budget.

Each of these is a real Warden milestone later — don't feel obliged.

---

## Submitting / tagging

- One repo. A README that opens with the problem and your approach, not install steps.
- Commit incrementally with real messages; each commit should build and run.
- **For Warden:** when the core is solid, tested, and the `DESIGN.md` is written, tag it **`v0.1-core`**. That tagged point is your take-home-sized artifact — a small thing done completely — and the foundation the MVP (`v0.2-mvp`) builds on.

> **Definition of done:** a reviewer clones it, runs it in under two minutes, reads a `DESIGN.md` that makes them nod, runs your tests and watches a duplicate command get applied exactly once and a no-ack command get redelivered — and comes away confident you'd handle the real thing.
