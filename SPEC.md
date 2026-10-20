# SPEC — Fleet Observer POC

## Problem
An operator is responsible for a fleet of worker processes. When one hangs, returns wrong results, or slows down, nothing tells them, so problems go unrecorded and nobody acts. The POC shows that one observer can detect each problem, **record it as an incident, and raise an on-screen alert that asks the operator to act**.

## First slice
The operator opens the dashboard and clicks **Start fleet**. Five simulated workers (worker-1…worker-5) each produce the first 17 primes (2…59), one at a time, with a random delay before each one. They loop forever. The observer checks every report and colors each worker's card: OK, warning (slow), or error. Now and then, a random fault scheduler makes a worker slow, wrong, or silent. Slow shows only as a warning on the card. A wrong answer or silence opens an **incident** and shows an **alert**. The incident stays until the operator **acknowledges** it *and* the worker is healthy again. Each card tails its worker's last 10 log entries.

## Out of scope
Real OS processes/containers · persistence across API restarts · auth/multi-user · external alert channels (email/Slack/pager) and escalation · changing fleet size in the UI · prime ranges other than the first 17 · log files or external sinks · incident history after resolution · charts and trends.

## Domain model
- **Fleet**: `state` (Stopped|Running), `size` = 5, `seed`
- **Worker**: `id` (immutable), `status`, `cycle`, `lastIndex` (1–17), `lastValue`, `lastLatencyMs`, `lastReportAt`, `okStreak`
- **Report**: `workerId`, `cycle`, `index`, `value`, `startedAt`, `receivedAt`
- **Verdict** (computed by the observer): `report`, `expected`, `correct`, `latencyMs`, `outcome` (Ok|TooSlow|WrongAnswer)
- **Incident**: `id`, `workerId`, `kind` (WrongAnswer|Unresponsive), `state`, `openedAt`, `acknowledgedAt?`, `recoveredAt?`, `resolvedAt?`
- **LogEntry**: `seq`, `timestamp`, `workerId`, `level` (Info|Warning|Error), `message`. Each worker keeps its last 10.
- **Thresholds**: slow > **500 ms** (warning only) · unresponsive = no report for **3 s** (error) · wrong answer (error) · healthy again after 3 consecutive Ok verdicts

## State machines
**Worker.status**

| From | To | Trigger |
|---|---|---|
| — | Starting | Fleet started |
| Starting | Healthy | First Ok verdict |
| Healthy | Slow | TooSlow verdict |
| Slow | Healthy | Next Ok verdict |
| Healthy / Slow | WrongAnswer | WrongAnswer verdict |
| any running state | Unresponsive | No report for 3 s |
| WrongAnswer / Unresponsive | Healthy | 3 consecutive Ok verdicts |
| any | Starting | Operator clicks "Restart worker" (same id) |
| any | Stopped | Fleet stopped |

**Incident** (opened when a worker enters WrongAnswer or Unresponsive)

| From | To | Trigger |
|---|---|---|
| Open | Acknowledged | Operator clicks "Acknowledge" |
| Open | Recovered | Worker returns to Healthy |
| Acknowledged | Resolved | Worker returns to Healthy |
| Recovered | Resolved | Operator clicks "Acknowledge" |
| Recovered | Open | Same fault kind recurs before acknowledgement |

## Invariants
1. A worker's `id` never changes once assigned, including across restarts and cycles.
2. A report, its verdict, and any incident it causes stay bound to the worker, cycle and index that produced it.
3. The observer computes the expected prime itself. It never trusts the worker's value or self-assessment.
4. An incident is Resolved only when it has been acknowledged **and** its worker is Healthy. No other path resolves it.
5. A worker has at most one unresolved incident per kind. Slow never creates an incident.
6. Every status change and every incident transition writes exactly one log entry for that worker. Within a worker, `seq` strictly increases.
7. Within a cycle, `index` goes 1→17 in strict order, then a new cycle restarts at 1.

## Simulated vs real
- **Simulated:** the workers are in-process background tasks in the API. Each prime is computed after a random base delay, and a **random fault scheduler** injects TooSlow, WrongAnswer or Unresponsive episodes. Both draw from one seeded RNG, so runs are reproducible. For tests, a Testing-environment-only endpoint (`POST /api/test/workers/{id}/fault`) forces or clears a fault deterministically and turns off random faults.
- **Real:** the observer verdicts, the state machines, incidents and alerts, the logs and the dashboard.

## Assumptions
- [ASSUMED] Base random delay is 50–400 ms, so a healthy worker stays under 500 ms.
- [ASSUMED] Fault rate: about one episode somewhere in the fleet every 15–20 s, each lasting 2–6 s. That is few, but enough to notice.
- [CONFIRMED] Alerts offer **Acknowledge** and **Restart worker**. A restart resets the cycle, keeps the id, and does not by itself resolve the incident.
- [CONFIRMED] An incident closes only when the worker is healthy **and** the operator has acknowledged it.
- [ASSUMED] Logs also go to the API console. The on-screen list keeps the last 10 entries per worker, newest at the bottom.

## Open questions
None open.
