import { expect, test } from '@playwright/test';

test('notices: write, see it live, end it, and persistence after reload', async ({ page }) => {
  const en = `Synthetic notice ${Date.now()}`;
  const de = 'Synthetische Meldung';

  await page.goto('/notices');
  await page.getByLabel('Info', { exact: true }).check({ force: true });

  await page.getByLabel('English text').fill(en);
  await expect(page.locator('#notice-en-count')).toHaveText(`${en.length}/280`);
  await page.getByLabel('German text').fill(de);
  await expect(page.locator('#notice-de-count')).toHaveText(`${de.length}/280`);
  await expect(page.getByRole('region', { name: 'Preview (English)' })).toContainText(en);
  await expect(page.getByRole('region', { name: 'Preview (German)' })).toContainText(de);

  await page.getByRole('button', { name: 'Publish', exact: true }).click();
  const confirm = page.getByRole('region', { name: 'Publish this notice to the app?' });
  await expect(confirm).toBeVisible();
  await confirm.getByRole('button', { name: 'Publish now' }).click();

  const status = page.getByRole('status');
  await expect(status).toHaveText(/^Published .+\.$/);
  const id = (await status.innerText()).replace(/^Published /, '').replace(/\.$/, '');

  const list = page.getByRole('region', { name: 'Notices in the app' });
  const item = list.getByRole('listitem', { name: id });
  await expect(item).toBeVisible();
  await expect(item).toContainText('Live');

  await item.getByRole('button', { name: 'End now' }).click();
  await expect(item).toContainText('Ended');
  await expect(item.getByRole('button', { name: 'End now' })).toHaveCount(0);

  await page.reload();
  const reloaded = page.getByRole('region', { name: 'Notices in the app' }).getByRole('listitem', { name: id });
  await expect(reloaded).toContainText('Ended');
  await expect(reloaded).toContainText(en);
});
