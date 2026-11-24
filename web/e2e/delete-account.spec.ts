import { expect, test } from '@playwright/test';
import { createHabit, createWebUser, SECRET } from './support/accounts';

test('deleting the account signs out and the credentials stop working', async ({ page }) => {
  const email = await createWebUser(page);
  await createHabit(page, 'Stretch');

  await page.goto('/settings/privacy');
  await page.getByLabel('Your password', { exact: true }).fill(SECRET);
  await page.getByLabel('Type DELETE to confirm').fill('DELETE');
  await page.getByRole('button', { name: 'Delete my account' }).click();

  await expect(page).toHaveURL(/\/login\?reason=deleted$/);
  await expect(page.getByRole('status').filter({ hasText: /deleted/i })).toBeVisible();

  await page.goto('/login');
  await page.getByLabel('Email', { exact: true }).fill(email);
  await page.getByLabel('Password', { exact: true }).fill(SECRET);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();

  await expect(page.getByRole('alert').filter({ hasText: /match/i })).toBeVisible();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByText(/session ended/i)).toHaveCount(0);
});

test('the public page signs in, deletes right there and ends the other session', async ({ page, browser }) => {
  const email = await createWebUser(page);
  const guest = await browser.newContext();
  try {
    const visitor = await guest.newPage();
    await visitor.goto('/delete-account');
    await expect(visitor.getByRole('heading', { level: 1, name: 'Delete your Ember account' })).toBeVisible();
    await expect(visitor.getByRole('button', { name: 'Delete my account' })).toHaveCount(0);

    await visitor.getByRole('link', { name: 'Sign in to delete your account' }).click();
    await expect(visitor).toHaveURL(/\/login\?returnUrl=%2Fdelete-account$/);
    await visitor.getByLabel('Email', { exact: true }).fill(email);
    await visitor.getByLabel('Password', { exact: true }).fill(SECRET);
    await visitor.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(visitor).toHaveURL(/\/delete-account$/);
    await expect(visitor.getByText(`You're signed in as ${email}.`)).toBeVisible();

    await visitor.getByLabel('Your password', { exact: true }).fill(`${SECRET}-wrong`);
    await visitor.getByLabel('Type DELETE to confirm').fill('DELETE');
    await visitor.getByRole('button', { name: 'Delete my account' }).click();
    await expect(visitor.getByText(/That password doesn't match/)).toBeVisible();
    expect((await page.request.get('/api/me')).status()).toBe(200);

    await visitor.getByLabel('Your password', { exact: true }).fill(SECRET);
    await visitor.getByRole('button', { name: 'Delete my account' }).click();
    await expect(visitor).toHaveURL(/\/login\?reason=deleted$/);
    await expect(visitor.getByRole('status').filter({ hasText: /deleted/i })).toBeVisible();

    expect((await page.request.get('/api/me')).status()).toBe(401);
    await visitor.goto('/delete-account');
    await expect(visitor.getByRole('link', { name: 'Sign in to delete your account' })).toBeVisible();
  } finally {
    await guest.close();
  }
});
