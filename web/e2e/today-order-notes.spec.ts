import { expect, test, type Page } from '@playwright/test';
import { addDays, createHabit, createWebUser, localToday } from './support/accounts';
import { dayButton, showMonthOf } from './support/history';
import { enableJournal, newPassphrase, openPage } from './support/security';

const dayNames = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

function weekdayName(date: string): string {
  return dayNames[new Date(`${date}T00:00:00Z`).getUTCDay()];
}

async function createTimedHabit(page: Page, name: string, time: string): Promise<void> {
  await page.goto('/habits/new');
  await page.getByRole('button', { name: 'Start from scratch' }).click();
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Reminder time').fill(time);
  await page.getByRole('button', { name: 'Create habit' }).click();
  await expect(page).toHaveURL(/\/today$/);
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
}

function forToday(page: Page) {
  return page.getByRole('region', { name: 'For today', exact: true }).getByRole('heading', { level: 3 });
}

async function todayOrder(page: Page): Promise<string[] | null> {
  const response = await page.request.get('/api/me');
  expect(response.ok()).toBeTruthy();
  return ((await response.json()) as { todayOrder?: string[] | null }).todayOrder ?? null;
}

async function rowMenu(page: Page, name: string, item: string): Promise<void> {
  await page.getByRole('button', { name: `Options for ${name}` }).click();
  await page.getByRole('menuitem', { name: item }).click();
}

async function savedOrder(page: Page, act: () => Promise<void>): Promise<void> {
  const saved = page.waitForResponse((r) => r.url().endsWith('/api/me/today-order') && r.request().method() === 'PUT');
  await act();
  expect((await saved).ok()).toBe(true);
}

test('Today lists only what is for today; a rest-day habit sits in a collapsed section that stays open on this device', async ({ page }) => {
  await createWebUser(page);
  const today = await localToday(page);
  await createHabit(page, 'Daily walk');

  await page.goto('/habits/new');
  await page.getByRole('button', { name: 'Start from scratch' }).click();
  await page.getByLabel('Name', { exact: true }).fill('Tomorrow swim');
  await page.getByText('On set days', { exact: true }).click();
  for (const day of dayNames) {
    const box = page.getByRole('checkbox', { name: day, exact: true });
    if ((await box.getAttribute('aria-checked')) === 'true') await box.click();
  }
  await page.getByRole('checkbox', { name: weekdayName(addDays(today, 1)), exact: true }).click();
  await page.getByRole('button', { name: 'Create habit' }).click();
  await expect(page).toHaveURL(/\/today$/);

  await expect(forToday(page)).toHaveText(['Daily walk']);
  const disclosure = page.getByRole('button', { name: 'Not for today · 1' });
  await expect(disclosure).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByRole('heading', { name: 'Tomorrow swim' })).toHaveCount(0);

  await disclosure.click();
  await expect(disclosure).toHaveAttribute('aria-expanded', 'true');
  const swim = page.getByRole('button', { name: 'Check in Tomorrow swim for today' });
  await swim.click();
  await expect(swim).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('alertdialog')).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(forToday(page)).toHaveText(['Daily walk']);

  await page.reload();
  await expect(page.getByRole('button', { name: 'Not for today · 1' })).toHaveAttribute('aria-expanded', 'true');
  await expect(page.getByRole('button', { name: 'Check in Tomorrow swim for today' })).toHaveAttribute('aria-pressed', 'true');
});

test('Today orders by reminder time, the row menu and a long-press drag reorder it and it is remembered, Sort by time again restores it', async ({ page }) => {
  await createWebUser(page);
  await createTimedHabit(page, 'Alpha', '09:00');
  await createTimedHabit(page, 'Bravo', '08:00');
  await createTimedHabit(page, 'Charlie', '10:00');

  await page.goto('/today');
  await expect(forToday(page)).toHaveText(['Bravo', 'Alpha', 'Charlie']);
  await expect(page.getByRole('button', { name: 'List options' })).toHaveCount(0);
  expect(await todayOrder(page)).toBeNull();

  await savedOrder(page, () => rowMenu(page, 'Bravo', 'Move down'));
  await expect(forToday(page)).toHaveText(['Alpha', 'Bravo', 'Charlie']);
  expect(await todayOrder(page)).toHaveLength(3);

  await page.reload();
  await expect(forToday(page)).toHaveText(['Alpha', 'Bravo', 'Charlie']);

  const first = await page.getByRole('heading', { name: 'Alpha', level: 3 }).boundingBox();
  const last = await page.getByRole('heading', { name: 'Charlie', level: 3 }).boundingBox();
  expect(first && last).toBeTruthy();
  await savedOrder(page, async () => {
    await page.mouse.move(first!.x + 4, first!.y + first!.height / 2);
    await page.mouse.down();
    await page.waitForTimeout(650);
    await page.mouse.move(last!.x + 4, last!.y + last!.height / 2 + 40, { steps: 8 });
    await page.mouse.up();
  });
  await expect(forToday(page)).toHaveText(['Bravo', 'Charlie', 'Alpha']);
  await page.reload();
  await expect(forToday(page)).toHaveText(['Bravo', 'Charlie', 'Alpha']);

  await savedOrder(page, async () => {
    await page.getByRole('button', { name: 'List options' }).click();
    await page.getByRole('menuitem', { name: 'Sort by time again' }).click();
  });
  await expect(forToday(page)).toHaveText(['Bravo', 'Alpha', 'Charlie']);
  await expect(page.getByRole('button', { name: 'List options' })).toHaveCount(0);
  expect(await todayOrder(page)).toBeNull();
  await page.reload();
  await expect(forToday(page)).toHaveText(['Bravo', 'Alpha', 'Charlie']);
});

test('a habit note is written from the row menu, read again in the sheet and in History', async ({ page }) => {
  await createWebUser(page);
  await createHabit(page, 'Stretch');
  const today = await localToday(page);
  const text = 'Felt loose after the second round';

  await rowMenu(page, 'Stretch', 'Note');
  const sheet = page.getByRole('dialog', { name: 'Stretch' });
  const field = sheet.getByLabel('A note for today, just for you');
  await expect(sheet.getByRole('button', { name: 'Save note' })).toHaveAttribute('aria-disabled', 'true');
  await field.fill(text);
  await expect(sheet.getByText(`${text.length} / 280`)).toBeVisible();
  await sheet.getByRole('button', { name: 'Save note' }).click();
  await expect(sheet).toHaveCount(0);

  await rowMenu(page, 'Stretch', 'Note');
  await expect(page.getByRole('dialog', { name: 'Stretch' }).getByLabel('A note for today, just for you')).toHaveValue(text);
  await page.getByRole('dialog', { name: 'Stretch' }).getByRole('button', { name: 'Not now' }).click();

  await page.getByRole('banner').getByRole('link', { name: 'History' }).click();
  await showMonthOf(page, today);
  await dayButton(page, today).click();
  const card = page.getByRole('region', { name: 'Note on Stretch' });
  await expect(card.getByText(text)).toBeVisible();
  await expect(card.getByRole('button', { name: 'Edit note' })).toBeVisible();
});

test('with the private journal on, a habit note is stored encrypted with no plaintext', async ({ page }) => {
  await createWebUser(page);
  await createHabit(page, 'Stretch');
  const today = await localToday(page);
  const text = 'Synthetic private words about stretching';
  await enableJournal(page, newPassphrase());
  await openPage(page, 'Today');

  await rowMenu(page, 'Stretch', 'Note');
  const sheet = page.getByRole('dialog', { name: 'Stretch' });
  await sheet.getByLabel('A note for today, just for you').fill(text);
  await sheet.getByRole('button', { name: 'Save note' }).click();
  await expect(sheet).toHaveCount(0);

  const stored = await page.evaluate(async (date) => {
    const response = await fetch(`/api/habit-notes?from=${date}&to=${date}`, { credentials: 'include' });
    return { ok: response.ok, body: await response.text() };
  }, today);
  expect(stored.ok).toBe(true);
  const notes = JSON.parse(stored.body) as { date: string; text?: string | null; encrypted?: unknown }[];
  expect(notes).toHaveLength(1);
  expect(notes[0].encrypted).toBeTruthy();
  expect(notes[0].text ?? null).toBeNull();
  expect(stored.body).not.toContain('Synthetic private words');

  await rowMenu(page, 'Stretch', 'Note');
  await expect(page.getByRole('dialog', { name: 'Stretch' }).getByLabel('A note for today, just for you')).toHaveValue(text);
});
