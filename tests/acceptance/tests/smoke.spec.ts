import { expect, test } from '@playwright/test'

test('home page shows API health', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByText('API: ok')).toBeVisible()
})
