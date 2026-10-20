# ADR 0001 — In-memory single-writer observer for the first slice

- **Status:** Accepted
- **Date:** 2026-10-19
- **Context docs:** [SPEC.md](../../SPEC.md), [ACCEPTANCE.md](../../ACCEPTANCE.md)

## Context

The first slice needs a fleet of 5 simulated workers. Each worker produces the first 17 primes in a loop. One observer must:

- spot wrong answers, slow reports (> 500 ms) and silent workers (no report for 3 s)
- record WrongAnswer and Unresponsive as incidents with on-screen alerts
- close an incident only when its worker is Healthy **and** the operator has acknowledged it

The operator can also restart a worker. Worker ids never change. Each worker card tails its last 10 log entries. The spec rules out persistence and real OS processes.

What both options share:

- **Two separate state machines.** The *observed* worker has its own lifecycle. The *observer* keeps its own view of each worker's health (Worker.status in SPEC.md) plus the Incident machine.
- **Data direction.** Data flows one way, from worker to observer. Workers never see the observer's verdicts. Control (restart/stop) flows from observer to worker on a separate path.
- **Silence detection.** The observer never pings workers. It detects silence only from how much time has passed since each worker's last report.
- **UI.** The UI polls `GET /api/fleet` for a snapshot about every 250 ms and sends commands with POST requests.

## Options considered

### Option A — In-memory observer that handles one message at a time (chosen)

All state lives in memory inside the API. Each worker is a background task that sends reports into one `Channel<ObserverMessage>`. A single observer loop reads that channel and is the **only** code that changes state. Clock ticks, operator commands (ack, restart, start/stop) and test-endpoint fault commands all go through the same channel.

**State machines and communication**

| Machine | States | Transitions and triggers |
|---|---|---|
| Worker (observed) | Idle → Running → Stopped | Start or Stop message on the worker's control channel. **Restart** cancels the task and starts a new one with the same id and `epoch + 1`. The fault mode (None/Slow/Wrong/Silent) is a setting the worker reads before each prime. It is not a state. |
| Observer's view of each worker | Starting, Healthy, Slow, WrongAnswer, Unresponsive, Stopped | As in SPEC.md. Triggers: a `Report` (verdict Ok/TooSlow/Wrong), a `Tick` (no report for 3 s), `Restart`, `Stop`. |
| Incident | Open, Acknowledged, Recovered, Resolved | `Ack` command, or the worker becoming Healthy, as in SPEC.md. |
| Fleet / observer | Stopped ↔ Running | Start fleet / Stop fleet. |

- **Worker → observer** (one way): `Report{workerId, epoch, cycle, index, value, startedAt}`.
- **Observer → worker** (one way, a separate control channel per worker): `Restart`, `Stop`.
- **UI ↔ API** (both ways): the UI pulls snapshots and pushes commands.

**Where invariants are enforced**

- **One writer.** Only the observer loop changes state, so ack, recovery and restart cannot race each other. This needs no locks or transactions.
- **Domain checks in `FleetState`:**
  - The expected prime comes from a fixed table of the first 17 primes. The worker's value is never trusted.
  - `index` must be exactly `last + 1`, or `1` when a new cycle starts. Anything else is logged as an error and ignored.
  - At most one unresolved incident per (worker, kind).
  - `Resolve()` can only run on the paths Acknowledged + Healthy, or Recovered + Ack.
- **Version checks:**
  - Each worker has an `epoch`. After a restart, a late report from the old task is dropped, so a report can never attach to the wrong run.
  - Each worker has a log `seq` that strictly increases.
  - Each snapshot carries a `version`, so the UI ignores poll responses that arrive out of order.
  - An ack carries the `incidentId`. Acknowledging the same incident twice does nothing extra.
- **Ids:** fixed `worker-1`…`worker-5`, created once when the fleet is built, and the field is read-only.
- **No database.**

**Controlling the simulation**

- **Faults:** each worker gets its faults from an `IFaultSource`.
  - `RandomFaultSource` uses a seeded `Random` and the fault rate from SPEC.md.
  - `ScriptedFaultSource` is used in the Testing environment. `POST /api/test/workers/{id}/fault` sets it, and it turns off random faults.
- **Time:** all timing (delays, the 3 s timeout, latency) goes through `TimeProvider`. xUnit tests use `FakeTimeProvider` to move time forward step by step with no real sleeping. Playwright runs on real time with faults forced through the test endpoint.

**Failure modes and what the user sees**

| Failure | What the operator sees |
|---|---|
| A worker task crashes | Reports stop, so the worker shows "Unresponsive" with an alert. It is detected the same way as a simulated silent fault. |
| The observer loop throws | Snapshots stop advancing. The UI shows an "Observer stalled" banner once the snapshot `version` stops changing for 3 s. |
| The API is down or polling fails | A "Connection lost — retrying" banner, with the last known cards greyed out. |
| The API restarts | All state is lost and the fleet comes back "Stopped". This is accepted, since persistence is out of scope. |
| Acknowledging an incident that just resolved | No error. The alert disappears on the next snapshot. |

**Trade-off:** this is the smallest design that meets every criterion. Handling one message at a time removes whole classes of race conditions, and `TimeProvider` with a seeded random generator makes the state machines fully testable in milliseconds. The cost is that nothing survives a restart, and "processes" are only tasks. It doesn't show the observer and workers failing independently or the network between them breaking.

### Option B — Workers report over HTTP, with state stored in SQLite (rejected for now)

Workers are HTTP clients that send `POST /api/reports`. They can run in-process or as a separate console process. The observer is a set of request handlers plus a background sweeper that runs every 500 ms and marks a worker Unresponsive when `now − lastReportAt > 3 s`. State is stored in SQLite through EF Core: `Workers`, `Reports`, `Incidents`, `LogEntries`, `FaultOverrides`.

- **Observer → worker** commands travel only in the response to a report (`continue` / `restart` / `stop`). Commands cannot reach a worker that has gone silent, so Restart has to stop and replace the worker's client.
- **Invariants in the database:**
  - Unique `(WorkerId, Epoch, Cycle, Index)` on Reports.
  - A partial unique index on Incidents `(WorkerId, Kind) WHERE State <> 'Resolved'`.
  - `CHECK (State <> 'Resolved' OR (AcknowledgedAt IS NOT NULL AND RecoveredAt IS NOT NULL))`.
  - `Workers.Id` is the primary key and is read-only after insert. Foreign keys from Reports and Incidents to Workers.
- **Concurrency:** optimistic concurrency with a `Version` column on Worker and Incident, since the handlers and the sweeper write concurrently. If an ack's version doesn't match, the API returns 409 and the UI shows "Alert changed — refreshed".
- **Faults:** read from a `FaultOverrides` row (written by the test endpoint), or from a seeded random schedule.
- **Failure modes:**
  - A worker that can't reach the API shows as Unresponsive.
  - A late sweeper can delay detection to about 3.5 s, or briefly report a worker as Unresponsive by mistake.
  - Lost SQLite writes show up as Unresponsive.
  - A 409 conflict on ack.
  - State survives an API restart.

**Trade-off:** this design is closer to a real fleet. It uses a real transport, keeps state across restarts, and enforces the invariants a second time in the database. The cost is more moving parts than the spec allows for. Concurrent writers need version checks and 409 handling. The sweeper interval and HTTP overhead make timing noisier, which puts normal latency closer to the 500 ms threshold. Tests are slower and less deterministic, and restarting a silent worker needs a separate path.

## Decision

Use **Option A**.

1. **It matches the confirmed scope.** Workers are simulated, there is no persistence, and the log keeps only 10 entries per worker. Most of what Option B adds is out of scope, or solves problems Option B creates for itself (concurrent writers, 409s, sweeper lag).
2. **It's easiest to verify.** Every rule that changes state runs one message at a time against `FakeTimeProvider` and a seeded random generator. AC-5 and AC-6 (incidents close only after recovery *and* acknowledgement) can be tested exactly in xUnit. Playwright only needs to confirm the UI shows the result.
3. **It's cheaper to change.** The worker-to-observer channel sits behind an `IReportSink` interface. If real processes or persistence are needed later, Option B's HTTP transport and SQLite storage can replace those parts without rewriting the observer or its state machines.

## Consequences

- The observer loop is the only writer. New behaviour must be added as a message type the loop handles, never as a direct change to state from an endpoint or a worker.
- An API restart loses all fleet state, incidents and logs. Revisit this if the stakeholder needs incident history (currently out of scope).
- `TimeProvider`, `IFaultSource` and `IReportSink` are required seams. Tests depend on them.
- The test fault endpoint must be registered only when `ASPNETCORE_ENVIRONMENT=Testing`.
- If real OS processes or persistence come into scope, write a new ADR that supersedes this one and reuses Option B's transport and database constraints.
