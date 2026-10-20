# ACCEPTANCE — Fleet Observer POC

Thresholds come from SPEC.md: slow > 500 ms (warning only), unresponsive = no report for 3 s, healthy again after 3 consecutive Ok verdicts.
Tests run with the API in the Testing environment, with a fixed seed and random faults off. They force or clear faults through `POST /api/test/workers/{id}/fault`. The criteria describe only what the operator sees.

## AC-1 Fleet of 5 starts healthy
**Given** the dashboard is open and the fleet is stopped
**When** the operator clicks "Start fleet"
**Then** exactly 5 worker cards appear, labelled "worker-1" … "worker-5", each showing status "Healthy" within 3 s, and "Active alerts" shows "No active alerts"

*Locators:* button "Start fleet" · region "Fleet" · article/heading "worker-1"…"worker-5" · status text "Healthy" · region "Active alerts" · text "No active alerts"

## AC-2 Workers loop through the first 17 primes
**Given** a running fleet with no faults
**When** the operator watches the "worker-1" card
**Then** "Last prime" only ever shows one of 2, 3, 5 … 59. "Progress" moves forward (e.g. "4 / 17"). "Latency" shows a value in ms below 500. After "17 / 17", progress returns to "1 / 17" and "Cycle" goes up by 1.

*Locators:* card "worker-1" · labels "Last prime", "Progress", "Latency", "Cycle"

## AC-3 Wrong answer opens an incident and alert
**Given** a running fleet with every worker "Healthy"
**When** worker-2 is forced to report wrong answers
**Then** the worker-2 card shows status "Wrong answer" styled as an error. "Active alerts" lists an alert containing "worker-2" and "Wrong answer" with buttons "Acknowledge" and "Restart worker". Worker-2's "Log" shows an Error entry like "cycle 1 #5 expected 11 got 12". All other cards stay "Healthy".

*Locators:* card "worker-2" · status "Wrong answer" · region "Active alerts" · alert containing "worker-2" · buttons "Acknowledge", "Restart worker" · list "Log" inside card "worker-2" · text "expected", "got"

## AC-4 Slow is a warning, not an alert
**Given** a running fleet with worker-1 "Healthy" and no active alerts
**When** worker-1 is forced to take longer than 500 ms per prime, then the fault is cleared
**Then** while slow, the worker-1 card shows status "Slow" styled as a warning, "Latency" is above 500 ms, and its "Log" has a Warning entry "Healthy → Slow", while "Active alerts" still shows "No active alerts". After the fault is cleared, the card returns to "Healthy".

*Locators:* status "Slow", "Healthy" · label "Latency" · list "Log" in card "worker-1" · text "Healthy → Slow" · text "No active alerts"

## AC-5 Incident closes only after recovery *and* acknowledgement (failure and recovery, invariant)
**Given** a running fleet with worker-3 "Healthy"
**When** worker-3 is forced silent for longer than 3 s, the fault is then cleared, and only after that does the operator click "Acknowledge" on its alert
**Then**:
- worker-3 shows "Unresponsive" within 4 s, and an alert for "worker-3" appears
- once its reports resume and it shows "Healthy", the alert **stays** in "Active alerts" marked "Recovered — awaiting acknowledgement"
- after "Acknowledge", the alert leaves "Active alerts"
- worker-3's "Log" shows, in order: "Healthy → Unresponsive", "Unresponsive → Healthy", "Incident resolved"

*Locators:* status "Unresponsive", "Healthy" · alert containing "worker-3" · text "Recovered — awaiting acknowledgement" · button "Acknowledge" · list "Log" in card "worker-3" entries in order

## AC-6 Acknowledging an unhealthy worker doesn't close the incident (invariant)
**Given** worker-2 is in "Wrong answer" with an active alert
**When** the operator clicks "Acknowledge" while the fault is still active, and the fault is cleared afterwards
**Then** the alert stays in "Active alerts" marked "Acknowledged" for as long as worker-2 is not "Healthy". It leaves once worker-2 shows "Healthy", and worker-2's "Log" then ends with "Incident resolved".

*Locators:* button "Acknowledge" · text "Acknowledged" · status "Wrong answer", "Healthy" · text "Incident resolved"

## AC-7 Worker identity is immutable and logs stay partitioned (invariant)
**Given** worker-4 has completed at least one full cycle
**When** the operator triggers "Restart worker" on worker-4 (via an incident on worker-4) and it runs another cycle
**Then** the card heading stays "worker-4". There are still exactly 5 cards, "worker-1"…"worker-5", with none added or renamed. Each card's "Log" lists at most 10 entries, newest last, all for that card's worker only.

*Locators:* headings "worker-1"…"worker-5" · button "Restart worker" · list "Log" in each card (≤ 10 items)

## AC-8 Stop the fleet
**Given** a running fleet
**When** the operator clicks "Stop fleet"
**Then** all 5 cards show "Stopped". No new log entries appear for 3 s. No "Unresponsive" status or alert is raised for stopped workers. "Start fleet" is enabled again.

*Locators:* button "Stop fleet" · status "Stopped" ×5 · region "Active alerts" · button "Start fleet" (enabled)
