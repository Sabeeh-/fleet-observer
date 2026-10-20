import { defineConfig, devices } from '@playwright/test'

// Acceptance runs its own API (Testing environment: scripted faults, test endpoints)
// and web dev server on separate ports, so it never collides with `npm run dev`.
export const API_URL = 'http://localhost:5111'
const WEB_PORT = 5174
const WEB_URL = `http://localhost:${WEB_PORT}`

export default defineConfig({
  testDir: './tests',
  // One shared fleet per server: run tests one at a time.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: 'list',
  use: {
    baseURL: WEB_URL,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'dotnet run --project ../../api/Api.csproj --no-launch-profile',
      url: `${API_URL}/api/health`,
      env: { ASPNETCORE_ENVIRONMENT: 'Testing', ASPNETCORE_URLS: API_URL },
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      command: 'npm --prefix ../../web run dev',
      url: WEB_URL,
      env: { API_URL, WEB_PORT: String(WEB_PORT) },
      reuseExistingServer: false,
      timeout: 60_000,
    },
  ],
})
