import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { createWebUser } from './support/accounts';

const tags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

async function expectAccessible(page: Page): Promise<void> {
  await page.evaluate(() => document.fonts.ready);
  await page.waitForFunction(() => !document.querySelector('main[data-arrive]'));
  const { violations } = await new AxeBuilder({ page }).withTags(tags).analyze();
  expect(violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([]);
}

test('a habit timer survives a reload, says when the time is up and checks in only on "Mark as done"', async ({ page }) => {
  await createWebUser(page);
  await page.goto('/habits/new');
  await page.getByRole('button', { name: 'Start from scratch' }).click();
  await page.getByLabel('Name', { exact: true }).fill('Walk');
  await page.getByLabel(/^Timer length/).fill('1');
  await page.getByRole('button', { name: 'Create habit' }).click();
  await expect(page).toHaveURL(/\/today$/);

  const checkIn = page.getByRole('button', { name: 'Check in Walk for today' });
  const timeLeft = page.getByRole('timer', { name: 'Time left for Walk' });
  await page.getByRole('button', { name: 'Start the 1 minute timer for Walk' }).click();
  await expect(timeLeft).toHaveText(/^0[01]:\d\d$/);
  await page.getByRole('button', { name: 'Pause the timer for Walk' }).click();
  await expect(page.getByRole('button', { name: 'Resume the timer for Walk' })).toBeFocused();

  await page.reload();
  await expect(timeLeft).toBeVisible();
  await expect(checkIn).toHaveAttribute('aria-pressed', 'false');
  await expectAccessible(page);
  await page.getByRole('button', { name: 'Resume the timer for Walk' }).click();

  await page.evaluate(() => {
    const stored = JSON.parse(localStorage.getItem('ember.habitTimer') ?? '{}') as object;
    localStorage.setItem('ember.habitTimer', JSON.stringify({ ...stored, endsAt: Date.now() - 1000 }));
    window.dispatchEvent(new Event('focus'));
  });
  const notice = page.getByRole('region', { name: 'Timer' });
  await expect(notice.getByText('Your time for Walk is up.')).toBeVisible();
  await expect(notice.getByRole('button', { name: 'Not now' })).toBeVisible();
  await expect(checkIn).toHaveAttribute('aria-pressed', 'false');
  await expectAccessible(page);

  await notice.getByRole('button', { name: 'Mark as done' }).click();
  await expect(checkIn).toHaveAttribute('aria-pressed', 'true');
  await expect(notice).toHaveCount(0);

  await page.reload();
  await expect(checkIn).toHaveAttribute('aria-pressed', 'true');
  await expect(notice).toHaveCount(0);
});
