import { expect, test } from '@playwright/test';
import { createWebUser } from './support/accounts';

test('Try again on the offline sign-in says Ember still cannot be reached, then goes on once the API answers', async ({ page }) => {
  await createWebUser(page);
  await page.addInitScript(() => localStorage.removeItem('ember.offlineMe'));
  await page.route('**/api/me', (route) => route.abort('internetdisconnected'));

  await page.goto('/today');
  await expect(page.getByRole('heading', { name: "Ember can't be reached right now" })).toBeVisible();
  const retry = page.getByRole('button', { name: 'Try again' });
  await retry.click();
  await expect(page.getByText("Ember still can't be reached. Check your connection and try again.")).toBeVisible();

  await page.unroute('**/api/me');
  await retry.click();
  await expect(page).toHaveURL(/\/today$/);
  await expect(page.getByRole('heading', { name: 'Today', exact: true })).toBeVisible();
});
