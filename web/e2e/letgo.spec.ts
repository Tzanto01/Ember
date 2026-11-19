import { expect, test, type Page } from '@playwright/test';
import { createHabit, createWebUser, localToday, setAccountLanguage } from './support/accounts';
import { dayButton, showMonthOf } from './support/history';

async function createLetGoHabit(page: Page, name: string): Promise<void> {
  await page.goto('/habits/new');
  await page.getByRole('button', { name: 'Something to let go of' }).click();
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByRole('button', { name: 'Create habit' }).click();
  await expect(page).toHaveURL(/\/today$/);
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
}

function letGoRow(page: Page, name: string) {
  return page.getByRole('listitem').filter({ has: page.getByRole('heading', { name, exact: true }) });
}

function slipDialog(page: Page) {
  return page.getByRole('dialog', { name: 'Noting a slip' });
}

test('a let-go habit has its own section below build habits and leaves their counts alone', async ({ page }) => {
  await createWebUser(page);
  await createHabit(page, 'Stretch');
  await page.getByRole('button', { name: 'Check in Stretch for today' }).click();
  await expect(page.getByRole('button', { name: 'Check in Stretch for today' })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByText('Done today', { exact: true })).toHaveCount(1);

  await createLetGoHabit(page, 'Late snacks');

  const section = page.getByRole('region', { name: 'Letting go' });
  await expect(section).toBeVisible();
  const row = letGoRow(page, 'Late snacks');
  await expect(row.getByText('1 day without')).toBeVisible();
  await expect(row.getByRole('button', { name: 'I slipped: Late snacks' })).toBeVisible();

  const stretch = await page.getByRole('heading', { name: 'Stretch', exact: true }).boundingBox();
  const heading = await page.getByRole('heading', { name: 'Letting go' }).boundingBox();
  expect(heading!.y).toBeGreaterThan(stretch!.y);

  await expect(page.getByRole('button', { name: 'Check in Stretch for today' })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByText('Done today', { exact: true })).toHaveCount(1);
  await expect(page.getByRole('button', { name: /Check in Late snacks/ })).toHaveCount(0);
});

test('a slip is noted with a reason, can be undone, and the sheet reopens clean', async ({ page }) => {
  await createWebUser(page);
  await createLetGoHabit(page, 'Late snacks');
  const row = letGoRow(page, 'Late snacks');
  await expect(row.getByText('1 day without')).toBeVisible();

  await row.getByRole('button', { name: 'I slipped: Late snacks' }).click();
  const sheet = slipDialog(page);
  await expect(sheet).toBeVisible();
  const bored = sheet.getByRole('button', { name: 'Bored', exact: true });
  await bored.click();
  await expect(bored).toHaveAttribute('aria-pressed', 'true');
  await sheet.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(sheet.getByRole('status')).toHaveText('Noted. Every day is a new start.');
  await expect(sheet.getByRole('status')).not.toContainText(/fail|broke|lost/i);

  await sheet.getByRole('button', { name: 'Undo' }).click();
  await expect(sheet.getByRole('status')).toHaveText('Taken back. Today has no slips noted.');
  await sheet.getByRole('button', { name: 'Done', exact: true }).click();
  await expect(sheet).toHaveCount(0);
  await expect(row.getByText('1 day without')).toBeVisible();

  await row.getByRole('button', { name: 'I slipped: Late snacks' }).click();
  await sheet.getByRole('button', { name: 'Tired', exact: true }).click();
  await sheet.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(sheet.getByRole('status')).toContainText(/^Noted\./);
  await sheet.getByRole('button', { name: 'Done', exact: true }).click();
  await expect(sheet).toHaveCount(0);
  await expect(row.getByText('Starting again, gently')).toBeVisible();

  await row.getByRole('button', { name: 'I slipped: Late snacks' }).click();
  await expect(sheet.getByRole('button', { name: 'Tired', exact: true })).toHaveAttribute('aria-pressed', 'false');
  await expect(sheet.getByRole('button', { name: 'Save', exact: true })).toBeVisible();
  await expect(sheet.getByRole('status')).toHaveCount(0);
});

test('history shows days without, longest stretch and days without this month, marks a slip softly and removes it from the day panel', async ({
  page,
}) => {
  await createWebUser(page);
  await createLetGoHabit(page, 'Late snacks');
  const today = await localToday(page);

  await page.getByRole('banner').getByRole('link', { name: 'History' }).click();
  await expect(page.getByRole('heading', { name: 'History', exact: true })).toBeVisible();
  await showMonthOf(page, today);
  await expect(dayButton(page, today)).toHaveAttribute('data-state', 'free');
  const tiles = page.locator('dl');
  await expect(tiles.getByText('Days without', { exact: true })).toBeVisible();
  await expect(tiles.getByText('1 day without', { exact: true })).toBeVisible();
  await expect(tiles.getByText('Longest stretch', { exact: true })).toBeVisible();
  await expect(tiles.getByText('Days without this month', { exact: true })).toBeVisible();

  await page.getByRole('banner').getByRole('link', { name: 'Today' }).click();
  const row = letGoRow(page, 'Late snacks');
  await row.getByRole('button', { name: 'I slipped: Late snacks' }).click();
  await slipDialog(page).getByRole('button', { name: 'Save', exact: true }).click();
  await expect(slipDialog(page).getByRole('status')).toContainText(/^Noted\./);
  await slipDialog(page).getByRole('button', { name: 'Done', exact: true }).click();

  await page.getByRole('banner').getByRole('link', { name: 'History' }).click();
  await showMonthOf(page, today);
  await expect(dayButton(page, today)).toHaveAttribute('data-state', 'slipped');
  await expect(dayButton(page, today)).toHaveAccessibleName(/one slip noted/);
  await expect(page.locator('dl').getByText('Starting again, gently')).toBeVisible();

  await dayButton(page, today).click();
  const panel = page.locator('#day-panel');
  await panel.getByRole('button', { name: "Remove this day's slips" }).click();
  await expect(dayButton(page, today)).toHaveAttribute('data-state', 'free');
  await expect(panel.getByRole('button', { name: "Remove this day's slips" })).toHaveCount(0);
  await expect(page.locator('dl').getByText('1 day without', { exact: true })).toBeVisible();
});

test('the craving feeling runs the urge-surfing practice, with no account', async ({ page }) => {
  await page.goto('/local');
  await page.getByRole('button', { name: 'Start without an account' }).click();
  await expect(page.getByRole('heading', { name: 'Today', exact: true })).toBeVisible();
  const apiRequests: string[] = [];
  page.on('request', (request) => {
    if (new URL(request.url()).pathname.startsWith('/api')) apiRequests.push(request.url());
  });

  await page.getByRole('link', { name: 'I need a moment' }).click();
  await page.getByRole('button', { name: "Name what's happening" }).click();
  await page.getByRole('button', { name: 'Craving, a strong urge' }).click();
  await page.getByRole('radio', { name: 'Strong', exact: true }).check();
  await page.getByRole('button', { name: 'Continue' }).click();
  await expect(page.getByRole('heading', { name: 'Surf the urge' })).toBeVisible();
  await expect(page.getByText('A craving is like a wave.')).toBeVisible();
  await page.getByRole('button', { name: 'Next' }).click();
  await expect(page.getByText(/Notice where you feel the urge in your body/)).toBeVisible();
  await page.getByRole('button', { name: 'Skip to the end' }).click();
  await expect(page.getByRole('heading', { name: 'How is it now?' })).toBeVisible();
  expect(apiRequests).toEqual([]);
});

test('the Today let-go row and slip sheet read in German', async ({ page }) => {
  await createWebUser(page);
  await createLetGoHabit(page, 'Spätsnacks');
  await setAccountLanguage(page, 'de');
  await page.goto('/today');

  await expect(page.getByRole('heading', { name: 'Loslassen' })).toBeVisible();
  const row = letGoRow(page, 'Spätsnacks');
  await expect(row.getByText('1 Tag ohne')).toBeVisible();
  await row.getByRole('button', { name: 'Ausgerutscht: Spätsnacks' }).click();
  const sheet = page.getByRole('dialog', { name: 'Einen Ausrutscher notieren' });
  await sheet.getByRole('button', { name: 'Müde', exact: true }).click();
  await sheet.getByRole('button', { name: 'Speichern', exact: true }).click();
  await expect(sheet.getByRole('status')).toContainText('Notiert.');
});

test('after a slip is saved the sheet offers Try instead with the built-in ideas and no screen-time button on the web', async ({ page }) => {
  await createWebUser(page);
  await createLetGoHabit(page, 'Late snacks');

  await letGoRow(page, 'Late snacks').getByRole('button', { name: 'I slipped: Late snacks' }).click();
  const sheet = slipDialog(page);
  await sheet.getByRole('button', { name: 'Bored', exact: true }).click();
  await sheet.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(sheet.getByRole('status')).toContainText(/^Noted\./);

  const ideas = sheet.getByRole('list', { name: 'Try instead' });
  await expect(ideas).toBeVisible();
  await expect(ideas.getByRole('listitem')).toHaveText([
    'Call or message someone',
    'Step outside for a few minutes',
    'Meet a friend',
    'Read a few pages',
    'Put the phone in another room',
  ]);
  await expect(page.getByRole('button', { name: 'See your screen time' })).toHaveCount(0);
});

test('the social-media-free evenings template creates a let-go habit with its quiet check-in at 21:00', async ({ page }) => {
  await createWebUser(page);
  await page.goto('/habits/new');
  const template = page.getByRole('button', { name: /Social-media-free evenings/ });
  await expect(template).toContainText('To let go of · quiet check-in at 21:00');
  await template.click();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue(/Social-media-free evenings/);
  await expect(page.getByLabel('Quiet evening check-in (optional)')).toHaveValue('21:00');
  await page.getByRole('button', { name: 'Create habit' }).click();
  await expect(page).toHaveURL(/\/today$/);
  await expect(page.getByRole('region', { name: 'Letting go' })).toBeVisible();
  await expect(page.getByRole('button', { name: /^I slipped: .*Social-media-free evenings/ })).toBeVisible();
  await page.getByRole('link', { name: /^Edit .*Social-media-free evenings/ }).click();
  await expect(page.getByLabel('Quiet evening check-in (optional)')).toHaveValue('21:00');
});
