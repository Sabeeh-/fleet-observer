# Fleet Observer

Minimal skeleton: ASP.NET Core minimal API + Vite/React/TS web app + Playwright acceptance tests. No domain code yet.

## Docs

- `SPEC.md` — problem, domain model, state machines, invariants
- `ACCEPTANCE.md` — acceptance criteria AC-1…AC-8 (with locators for Playwright)
- `docs/adr/` — architecture decisions. ADR 0001: in-memory observer loop that is the only code changing state; read it before changing the observer.

## Layout

- `api/` — ASP.NET Core minimal API (.NET 10). Endpoint: `GET /api/health` → `{"status":"ok"}`.
- `api.tests/` — xUnit tests using `WebApplicationFactory<Program>`.
- `web/` — Vite + React + TypeScript. Dev server proxies `/api` → the API.
- `tests/acceptance/` — Playwright (chromium only). Its `webServer` config starts both apps itself.
- `FleetObserver.slnx` — solution containing `api` and `api.tests`.

## URLs (dev)

- API: http://localhost:5110 (health: http://localhost:5110/api/health)
- Web: http://localhost:5173 (calls `/api/*` through the Vite proxy)

Ports are fixed in `api/Properties/launchSettings.json` and `web/vite.config.ts` (`strictPort`); change both places plus `tests/acceptance/playwright.config.ts` together.

## Commands (run from repo root)

- `npm run setup` — install all npm deps, Playwright chromium, and restore .NET packages (first time only)
- `npm run dev` — run API and web together (concurrently; Ctrl+C stops both)
- `npm run check` — run every check, in order:
  - `npm run check:api` — `dotnet test FleetObserver.slnx`
  - `npm run check:web` — oxlint + `tsc -b && vite build` in `web/`
  - `npm run check:acceptance` — Playwright smoke test (starts both apps, or reuses ones already running on those ports when not in CI)
