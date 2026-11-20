import { expect, test, type Page } from '@playwright/test';
import { createWebUser } from './support/accounts';
import { seedLocalMode, settled, visualNow } from './support/visual';

test.skip(process.platform !== 'linux', 'The baselines are Linux Chromium renders.');

const sizes = [
  { name: 'phone', width: 390, height: 844 },
  { name: 'desktop', width: 1280, height: 800 },
];
const schemes = ['light', 'dark'] as const;
const comparison = { animations: 'disabled', caret: 'hide', maxDiffPixels: 50 } as const;

interface Screen {
  name: string;
  path: string;
  heading: RegExp | string;
}

const screens: Screen[] = [
  { name: 'today', path: '/today', heading: 'Today' },
  { name: 'history', path: '/history/1', heading: 'History' },
  { name: 'settings', path: '/settings', heading: /^(Settings|Account & security)$/ },
  { name: 'habit-new', path: '/habits/new', heading: /new habit/i },
  { name: 'moment', path: '/moment', heading: /^You're here/ },
  { name: 'privacy', path: '/legal/privacy', heading: /^Datenschutzerklärung/ },
  { name: 'terms', path: '/legal/terms', heading: /^Nutzungsbedingungen/ },
  { name: 'imprint', path: '/legal/imprint', heading: 'Impressum' },
];

async function turnOnFireside(page: Page): Promise<void> {
  await page.goto('/settings/people');
  await page.getByLabel('Your name at the fireside').fill('Ada');
  await page.locator('#fireside-enabled').click();
  await page.getByRole('button', { name: 'Agree and turn on' }).click();
  await expect(page.locator('#fireside-enabled')).toBeChecked();
}

for (const { name: size, ...viewport } of sizes) {
  for (const colorScheme of schemes) {
    test.describe(`${size} ${colorScheme}`, () => {
      test.use({
        viewport,
        colorScheme,
        reducedMotion: 'reduce',
        deviceScaleFactor: 1,
        locale: 'en-US',
        timezoneId: 'UTC',
      });

      test.beforeEach(async ({ page }) => {
        await page.clock.setFixedTime(visualNow);
        await page.addInitScript(
          (scheme) => localStorage.setItem('ember.colorScheme', scheme),
          colorScheme,
        );
      });

      for (const screen of screens) {
        test(screen.name, async ({ page }) => {
          await seedLocalMode(page);
          await page.goto(screen.path);
          await expect(page.getByRole('heading', { level: 1, name: screen.heading })).toBeVisible();
          await settled(page);
          await expect(page).toHaveScreenshot(
            `${screen.name}-${size}-${colorScheme}.png`,
            comparison,
          );
        });
      }

      test('fireside', async ({ page }) => {
        await createWebUser(page);
        await turnOnFireside(page);
        await page.goto('/fireside');
        await expect(page.getByRole('heading', { level: 1, name: 'Fireside' })).toBeVisible();
        await settled(page);
        await expect(page).toHaveScreenshot(`fireside-${size}-${colorScheme}.png`, comparison);
      });
    });
  }
}
