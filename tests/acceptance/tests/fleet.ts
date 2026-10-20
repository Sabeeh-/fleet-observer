import { expect, type APIRequestContext, type Page } from '@playwright/test'
import { API_URL } from '../playwright.config'

export type Fault = 'None' | 'Slow' | 'WrongAnswer' | 'Unresponsive'

export const PRIMES = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59]
export const WORKER_IDS = ['worker-1', 'worker-2', 'worker-3', 'worker-4', 'worker-5']

/** Test-only control of the simulated workers (Testing environment endpoint). */
export async function setFault(request: APIRequestContext, workerId: string, mode: Fault) {
  const res = await request.post(`${API_URL}/api/test/workers/${workerId}/fault`, { data: { mode } })
  expect(res.ok()).toBeTruthy()
}

export async function resetFleet(request: APIRequestContext) {
  const res = await request.post(`${API_URL}/api/test/reset`)
  expect(res.ok()).toBeTruthy()
}

export function fleetRegion(page: Page) {
  return page.getByRole('region', { name: 'Fleet' })
}

export function card(page: Page, workerId: string) {
  return fleetRegion(page).getByRole('article', { name: workerId, exact: true })
}

export function status(page: Page, workerId: string) {
  return card(page, workerId).getByLabel('Status')
}

export function field(page: Page, workerId: string, label: string) {
  return card(page, workerId).getByLabel(label, { exact: true })
}

export function log(page: Page, workerId: string) {
  return card(page, workerId).getByRole('list', { name: 'Log' }).getByRole('listitem')
}

export function alerts(page: Page) {
  return page.getByRole('region', { name: 'Active alerts' })
}

export function alertFor(page: Page, workerId: string) {
  return alerts(page).getByRole('listitem').filter({ hasText: workerId })
}

export async function startHealthyFleet(page: Page, request: APIRequestContext) {
  await resetFleet(request)
  await page.goto('/')
  await page.getByRole('button', { name: 'Start fleet' }).click()
  for (const id of WORKER_IDS) {
    await expect(status(page, id)).toHaveText(/Healthy/, { timeout: 3_000 })
  }
}

/** Asserts the given messages appear in this order among the worker's log entries. */
export async function expectLogOrder(page: Page, workerId: string, messages: string[]) {
  await expect
    .poll(async () => {
      const lines = await log(page, workerId).allInnerTexts()
      const positions = messages.map((m) => lines.findIndex((line) => line.includes(m)))
      return positions.every((p, i) => p >= 0 && (i === 0 || p > positions[i - 1]))
    })
    .toBe(true)
}
