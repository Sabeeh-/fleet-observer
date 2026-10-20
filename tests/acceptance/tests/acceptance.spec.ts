import { expect, test } from '@playwright/test'
import {
  PRIMES,
  WORKER_IDS,
  alertFor,
  alerts,
  card,
  expectLogOrder,
  field,
  fleetRegion,
  log,
  resetFleet,
  setFault,
  startHealthyFleet,
  status,
} from './fleet'

test.beforeEach(async ({ request }) => {
  await resetFleet(request)
})

test('AC-1 fleet of 5 starts healthy', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('button', { name: 'Start fleet' })).toBeEnabled()
  await page.getByRole('button', { name: 'Start fleet' }).click()

  await expect(fleetRegion(page).getByRole('article')).toHaveCount(5)
  for (const id of WORKER_IDS) {
    await expect(fleetRegion(page).getByRole('heading', { name: id, exact: true })).toBeVisible()
    await expect(status(page, id)).toHaveText(/Healthy/, { timeout: 3_000 })
  }
  await expect(alerts(page).getByText('No active alerts')).toBeVisible()
})

test('AC-2 workers loop through the first 17 primes', async ({ page, request }) => {
  await startHealthyFleet(page, request)

  let previous = { cycle: 1, index: 0 }
  let sawWrap = false
  const deadline = Date.now() + 12_000
  while (!sawWrap && Date.now() < deadline) {
    const prime = await field(page, 'worker-1', 'Last prime').innerText()
    const progress = await field(page, 'worker-1', 'Progress').innerText()
    const cycle = Number(await field(page, 'worker-1', 'Cycle').innerText())
    const latency = await field(page, 'worker-1', 'Latency').innerText()
    const match = /^(\d+) \/ 17$/.exec(progress)
    expect(match, progress).not.toBeNull()
    const index = Number(match![1])

    if (index > 0) {
      expect(PRIMES).toContain(Number(prime))
      expect(Number(prime)).toBe(PRIMES[index - 1])
      expect(Number(/^(\d+) ms$/.exec(latency)?.[1])).toBeLessThan(500)
    }
    if (cycle === previous.cycle) {
      expect(index).toBeGreaterThanOrEqual(previous.index)
    } else {
      expect(cycle).toBe(previous.cycle + 1)
      expect(index).toBeLessThan(previous.index)
      sawWrap = true
    }
    previous = { cycle, index }
    await page.waitForTimeout(100)
  }
  expect(sawWrap, 'cycle should advance after 17 / 17').toBe(true)
})

test('AC-3 wrong answer opens an incident and alert', async ({ page, request }) => {
  await startHealthyFleet(page, request)
  await setFault(request, 'worker-2', 'WrongAnswer')

  await expect(status(page, 'worker-2')).toHaveText(/Wrong answer/)
  await expect(card(page, 'worker-2')).toHaveClass(/card-error/)
  const alert = alertFor(page, 'worker-2')
  await expect(alert).toContainText('Wrong answer')
  await expect(alert.getByRole('button', { name: 'Acknowledge' })).toBeVisible()
  await expect(alert.getByRole('button', { name: 'Restart worker' })).toBeVisible()
  await expect(log(page, 'worker-2').filter({ hasText: /ERROR.*cycle \d+ #\d+ expected \d+ got \d+/ })).toHaveCount(1)

  for (const id of WORKER_IDS.filter((w) => w !== 'worker-2')) {
    await expect(status(page, id)).toHaveText(/Healthy/)
  }
})

test('AC-4 slow is a warning, not an alert', async ({ page, request }) => {
  await startHealthyFleet(page, request)
  await setFault(request, 'worker-1', 'Slow')

  await expect(status(page, 'worker-1')).toHaveText(/Slow/)
  await expect(card(page, 'worker-1')).toHaveClass(/card-warning/)
  await expect
    .poll(async () => Number(/(\d+) ms/.exec(await field(page, 'worker-1', 'Latency').innerText())?.[1]))
    .toBeGreaterThan(500)
  await expect(log(page, 'worker-1').filter({ hasText: /WARNING.*Healthy → Slow/ })).toHaveCount(1)
  await expect(alerts(page).getByText('No active alerts')).toBeVisible()

  await setFault(request, 'worker-1', 'None')
  await expect(status(page, 'worker-1')).toHaveText(/Healthy/)
  await expect(alerts(page).getByText('No active alerts')).toBeVisible()
})

test('AC-5 incident closes only after recovery and acknowledgement', async ({ page, request }) => {
  await startHealthyFleet(page, request)
  await setFault(request, 'worker-3', 'Unresponsive')

  await expect(status(page, 'worker-3')).toHaveText(/Unresponsive/, { timeout: 4_500 })
  const alert = alertFor(page, 'worker-3')
  await expect(alert).toBeVisible()

  await setFault(request, 'worker-3', 'None')
  await expect(status(page, 'worker-3')).toHaveText(/Healthy/)
  await expect(alert).toContainText('Recovered — awaiting acknowledgement')
  await page.waitForTimeout(1_000)
  await expect(alert).toBeVisible() // still waiting for the operator

  await alert.getByRole('button', { name: 'Acknowledge' }).click()
  await expect(alert).toHaveCount(0)
  await expectLogOrder(page, 'worker-3', ['Healthy → Unresponsive', 'Unresponsive → Healthy', 'Incident resolved'])
})

test('AC-6 acknowledging an unhealthy worker does not close the incident', async ({ page, request }) => {
  await startHealthyFleet(page, request)
  await setFault(request, 'worker-2', 'WrongAnswer')
  const alert = alertFor(page, 'worker-2')
  await expect(alert).toBeVisible()

  await alert.getByRole('button', { name: 'Acknowledge' }).click()
  await expect(alert).toContainText('Acknowledged')
  await page.waitForTimeout(1_000)
  await expect(status(page, 'worker-2')).toHaveText(/Wrong answer/)
  await expect(alert).toBeVisible()

  await setFault(request, 'worker-2', 'None')
  await expect(status(page, 'worker-2')).toHaveText(/Healthy/)
  await expect(alert).toHaveCount(0)
  await expect(log(page, 'worker-2').last()).toContainText('Incident resolved')
})

test('AC-7 worker identity is immutable and logs stay partitioned', async ({ page, request }) => {
  await startHealthyFleet(page, request)
  await expect(field(page, 'worker-4', 'Cycle')).toHaveText('2', { timeout: 12_000 })

  await setFault(request, 'worker-4', 'WrongAnswer')
  const alert = alertFor(page, 'worker-4')
  await alert.getByRole('button', { name: 'Restart worker' }).click()
  await setFault(request, 'worker-4', 'None')
  await expect(log(page, 'worker-4').filter({ hasText: 'Restart requested' })).toHaveCount(1)
  await expect(status(page, 'worker-4')).toHaveText(/Healthy/)
  await expect(field(page, 'worker-4', 'Cycle')).toHaveText('2', { timeout: 12_000 })

  const headings = fleetRegion(page).getByRole('heading', { level: 2 })
  await expect(headings).toHaveText(WORKER_IDS)
  for (const id of WORKER_IDS) {
    const lines = await log(page, id).allInnerTexts()
    expect(lines.length).toBeLessThanOrEqual(10)
    for (const line of lines) {
      expect(line).toContain(id)
      for (const other of WORKER_IDS.filter((w) => w !== id)) expect(line).not.toContain(other)
    }
  }
})

test('AC-8 stop the fleet', async ({ page, request }) => {
  await startHealthyFleet(page, request)
  await page.getByRole('button', { name: 'Stop fleet' }).click()

  for (const id of WORKER_IDS) {
    await expect(status(page, id)).toHaveText(/Stopped/)
  }
  const before = await Promise.all(WORKER_IDS.map((id) => log(page, id).allInnerTexts()))
  await page.waitForTimeout(3_500)
  const after = await Promise.all(WORKER_IDS.map((id) => log(page, id).allInnerTexts()))
  expect(after).toEqual(before)

  for (const id of WORKER_IDS) {
    await expect(status(page, id)).toHaveText(/Stopped/)
  }
  await expect(alerts(page).getByText('No active alerts')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Start fleet' })).toBeEnabled()
})
