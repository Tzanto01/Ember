import { expect, test } from '@playwright/test';

const author = 'e2e@example.test';

test('board: keyboard move, drag, comment, and persistence after reload', async ({ page }) => {
  const keyboardTitle = 'Synthetic report 1';
  const dragTitle = 'Synthetic report 2';
  const comment = `E2E comment ${Date.now()}`;

  await page.goto('/feedback');
  const planned = page.getByRole('region', { name: 'Planned' });
  const implemented = page.getByRole('region', { name: 'Implemented' });
  await expect(page.getByRole('region', { name: 'Open' }).getByRole('article', { name: keyboardTitle })).toBeVisible();

  const keyboardCard = page.getByRole('article', { name: keyboardTitle });
  await keyboardCard.getByRole('button', { name: 'Move', exact: true }).focus();
  await page.keyboard.press('Enter');
  await page.getByRole('button', { name: 'Move to Planned' }).focus();
  await page.keyboard.press('Enter');
  await expect(planned.getByRole('article', { name: keyboardTitle })).toBeVisible();

  await page.getByRole('region', { name: 'Open' }).getByRole('article', { name: dragTitle }).dragTo(implemented);
  await expect(implemented.getByRole('article', { name: dragTitle })).toBeVisible();

  await planned.getByRole('article', { name: keyboardTitle }).getByRole('button', { name: keyboardTitle }).click();
  const dialog = page.getByRole('dialog', { name: keyboardTitle });
  await dialog.getByLabel('Add a comment').fill(comment);
  await dialog.getByRole('button', { name: 'Add comment' }).click();
  await expect(dialog.getByText(comment)).toBeVisible();

  await page.reload();
  await expect(page.getByRole('region', { name: 'Planned' }).getByRole('article', { name: keyboardTitle })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Implemented' }).getByRole('article', { name: dragTitle })).toBeVisible();

  await page.getByRole('article', { name: keyboardTitle }).getByRole('button', { name: keyboardTitle }).click();
  const reopened = page.getByRole('dialog', { name: keyboardTitle });
  const entry = reopened.getByText(comment);
  await expect(entry).toBeVisible();
  await expect(reopened.getByText(author).first()).toBeVisible();
});

test('a request without the identity header is rejected with 403', async ({ playwright, baseURL }) => {
  const anonymous = await playwright.request.newContext({ baseURL, extraHTTPHeaders: {} });
  const response = await anonymous.get('/api/session');
  expect(response.status()).toBe(403);
  await anonymous.dispose();
});
