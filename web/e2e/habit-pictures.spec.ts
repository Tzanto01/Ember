import { expect, test, type Page } from '@playwright/test';
import { createWebUser } from './support/accounts';

async function createBottleHabit(page: Page): Promise<void> {
  await page.goto('/habits/new');
  await page.getByRole('button', { name: 'Start from scratch' }).click();
  await page.getByLabel('Name', { exact: true }).fill('Drink water');
  await expect(page.getByText('Want to see this one as a bottle you fill up?')).toBeVisible();
  await page.getByRole('button', { name: 'Try the bottle' }).click();
  await expect(page.getByRole('radio', { name: 'Bottle' })).toBeChecked();
  await expect(page.getByLabel('Daily goal')).toHaveValue('2');
  await expect(page.getByLabel('Each tap counts as')).toHaveValue('250');
  await expect(page.getByText("That's 8 taps a day.")).toBeVisible();
  await page.getByRole('button', { name: 'Create habit' }).click();
  await expect(page).toHaveURL(/\/today$/);
}

async function tapThroughTheDay(page: Page): Promise<void> {
  const bottle = page.getByRole('button', { name: /^Drink water, \d of 8 glasses\./ });
  const progress = page.locator('p[aria-live="polite"]').filter({ hasText: /^\d of 8, / });
  await expect(page.getByRole('heading', { name: 'Drink water', exact: true })).toBeVisible();

  await bottle.click();
  await expect(progress).toHaveText('1 of 8, 250 ml');
  await bottle.click();
  await expect(progress).toHaveText('2 of 8, 500 ml');
  await expect(bottle).toHaveAccessibleName('Drink water, 2 of 8 glasses. Add 250 ml.');

  const more = page.getByRole('button', { name: 'More for Drink water' });
  await more.focus();
  await page.keyboard.press('Enter');
  const choices = page.getByRole('group', { name: 'Amounts for Drink water' });
  await expect(choices.getByRole('button', { name: '+250 ml' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(choices).toHaveCount(0);
  await expect(more).toBeFocused();

  await page.keyboard.press('Enter');
  await choices.getByRole('button', { name: '+750 ml' }).click();
  await expect(progress).toHaveText('5 of 8, 1.25 L');

  await page.getByRole('button', { name: 'Take one check-in off Drink water' }).click();
  await expect(progress).toHaveText('4 of 8, 1 L');
}

test('a bottle habit goes from the new-habit offer to tapped, topped-up and undone check-ins', async ({ page }) => {
  await createWebUser(page);
  await createBottleHabit(page);
  await tapThroughTheDay(page);

  await page.reload();
  await expect(page.locator('p[aria-live="polite"]').filter({ hasText: /^\d of 8, / })).toHaveText('4 of 8, 1 L');
});

test('the same bottle journey works without an account', async ({ page }) => {
  await page.goto('/local');
  await page.getByRole('button', { name: 'Start without an account' }).click();
  await expect(page.getByRole('heading', { name: 'Today', exact: true })).toBeVisible();
  await createBottleHabit(page);
  await tapThroughTheDay(page);
});
