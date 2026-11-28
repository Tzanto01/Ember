import { expect, test } from '@playwright/test';
import { createHabit, createWebUser } from './support/accounts';

test('every sub page leads back up to its parent, and the main nav lights the parent tab and reaches the others', async ({
  page,
}) => {
  await createWebUser(page);
  await createHabit(page, 'Stretch');
  const tab = (name: string) =>
    page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name, exact: true });
  const title = (name: string) => page.getByRole('heading', { level: 1, name, exact: true });
  const back = (name: string) => page.getByLabel(name, { exact: true }).click();

  await page.getByRole('link', { name: 'New habit' }).click();
  await expect(title('New habit')).toBeVisible();
  await expect(tab('Today')).toHaveAttribute('aria-current', 'page');
  await back('Back to Today');
  await expect(page).toHaveURL(/\/today$/);

  await page.getByRole('link', { name: 'Edit Stretch' }).click();
  await expect(title('Edit habit')).toBeVisible();
  await back('Back to Today');
  await expect(page).toHaveURL(/\/today$/);

  await page.getByRole('link', { name: 'Routines', exact: true }).click();
  await expect(title('Routines')).toBeVisible();
  await expect(tab('Today')).toHaveAttribute('aria-current', 'page');
  await page.getByRole('link', { name: 'New routine' }).click();
  await expect(title('New routine')).toBeVisible();
  await page.getByLabel('Name', { exact: true }).fill('Morning');
  await page.getByRole('button', { name: 'Add Stretch' }).click();
  await page.getByRole('button', { name: 'Create routine' }).click();
  await expect(page).toHaveURL(/\/routines$/);
  await page.getByRole('link', { name: 'Edit Morning routine' }).click();
  await expect(title('Edit routine')).toBeVisible();
  await back('Back to routines');
  await expect(title('Routines')).toBeVisible();
  await back('Back to Today');
  await expect(page).toHaveURL(/\/today$/);

  await page.goto('/routines/new');
  await tab('History').click();
  await expect(page).toHaveURL(/\/history$/);
  await expect(tab('History')).toHaveAttribute('aria-current', 'page');
  await tab('Settings').click();
  await expect(page).toHaveURL(/\/settings/);

  await page.getByRole('link', { name: 'Feedback & about', exact: true }).click();
  await page.getByRole('link', { name: "What's new", exact: true }).click();
  await expect(title("What's new")).toBeVisible();
  await expect(tab('Settings')).toHaveAttribute('aria-current', 'page');
  await back('Back to Settings');
  await expect(page).toHaveURL(/\/settings(\/account)?$/);

  await page.getByRole('link', { name: 'Journal & reflections', exact: true }).click();
  await page.getByRole('link', { name: 'Open Your moments' }).click();
  await expect(title('Your moments')).toBeVisible();
  await expect(tab('Settings')).toHaveAttribute('aria-current', 'page');
  await back('Back to Settings');
  await expect(page).toHaveURL(/\/settings(\/account)?$/);

  await page.goto('/moments');
  await tab('Today').click();
  await expect(page).toHaveURL(/\/today$/);
});
