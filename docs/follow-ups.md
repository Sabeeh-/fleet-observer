# Follow-ups

Open items from building the first slice (ADR 0001). Remove each one when it's done.

## 1. Rework acceptance tests so they don't depend on the implementation

The Playwright tests in `tests/acceptance/tests/acceptance.spec.ts` were written by the same session that built the code, after the code existed. They rely on implementation details instead of ACCEPTANCE.md alone:

- CSS classes (`card-error`, `card-warning`) used to check styling
- exact log wording and level prefixes (`ERROR`, `WARNING`, `Incident resolved: …`)
- the `Status` aria-label on the status pill
- the worker-id prefix on each log line, which was added so AC-7 could be tested
- `POST /api/test/reset`, which SPEC.md doesn't define

Plan: have the `acceptance-tester` agent write the tests from ACCEPTANCE.md and SPEC.md only, without reading `api/` or `web/`. Run them against the build, then for each failure decide whether the code or the criterion is wrong and fix that side.

## 2. Update the docs to match what was built

- **CLAUDE.md:** remove "No domain code yet". Document that acceptance runs its own API on port 5111 (Testing environment) and web server on port 5174 with `reuseExistingServer: false`. List the fleet endpoints and the Testing-only `/api/test/workers/{id}/fault` and `/api/test/reset`.
- **ADR 0001:** xUnit tests pass explicit timestamps into `FleetState` instead of using `FakeTimeProvider`. Record this deviation.
- **SPEC.md / ACCEPTANCE.md:** either specify the test reset endpoint, the log line format (worker id, level, message) and the status display names, or make sure the reworked tests don't depend on them.
