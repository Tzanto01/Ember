import { expect, test } from '@playwright/test';
import { createHabit, createWebUser, uniqueSuffix } from './support/accounts';

test('withdrawing consent leaves only the export, and agreeing again brings the habits back', async ({
  page,
}) => {
  const habit = `Water ${uniqueSuffix()}`;
  await createWebUser(page);
  await createHabit(page, habit);

  await page.goto('/settings/privacy');
  const card = page.getByRole('region', { name: 'Privacy' });
  await card.getByRole('button', { name: 'Withdraw consent' }).click();
  await card.getByRole('button', { name: 'Withdraw', exact: true }).click();

  await expect(page).toHaveURL(/\/consent$/);
  await expect(
    page.getByRole('heading', { name: "Ember isn't saving anything right now" }),
  ).toBeVisible();
  await expect(page.getByText(/Ember deletes it on /)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Download JSON' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Download CSV' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'No thanks' })).toHaveCount(0);

  await page.goto('/today');
  await expect(page).toHaveURL(/\/consent$/);

  await page.getByRole('checkbox', { name: "I'm at least 16" }).check();
  await page.getByRole('button', { name: 'I agree' }).click();
  await expect(page).toHaveURL(/\/today$/);
  await expect(page.getByRole('heading', { name: habit, exact: true })).toBeVisible();
});
