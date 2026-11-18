import { expect, type Page, test } from '@playwright/test';

const maintenance = { type: 'maintenance', title: 'Ember is resting', status: 503 };
const noticeText = 'Ember is resting for a short update. Your check-ins are saved on this device.';

async function answerLikeTheFallback(page: Page): Promise<void> {
  await page.route(
    (url) => url.pathname.startsWith('/api/'),
    (route) =>
      route.fulfill({
        status: 503,
        contentType: 'application/problem+json',
        headers: { 'X-Ember-Fallback': '1', 'Retry-After': '120', 'Cache-Control': 'no-store' },
        body: JSON.stringify(maintenance),
      }),
  );
  await page.route('**/notices.json', (route) =>
    route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        notices: [
          { id: 'rest-synthetic', kind: 'maintenance', en: noticeText, de: 'Ember ruht kurz.' },
        ],
      }),
    }),
  );
}


test('while the fallback answers the API, a signed-in user keeps the app, sees the notice and can open "I need a moment"', async ({
  page,
}) => {
  await page.addInitScript(() => {
    const timezone = 'Europe/Berlin';
    const localToday = new Intl.DateTimeFormat('en-CA', { timeZone: timezone }).format(new Date());
    localStorage.setItem(
      'ember.offlineMe',
      JSON.stringify({
        id: 'synthetic-user',
        email: 'resting@example.test',
        emailConfirmed: true,
        hasPassword: true,
        discord: { linked: false, id: null },
        timezone,
        timezoneSet: true,
        localToday,
        graceDaysPerWeek: 1,
        secondReminderMinutes: 0,
        theme: null,
        consent: { given: true, policyVersion: '1', currentPolicyVersion: '1' },
        dmReminders: { paused: false },
        journal: { encrypted: false, turningOff: false, plaintextLeft: 0, encryptedLeft: 0 },
        showStreaks: true,
        language: 'en',
      }),
    );
  });
  await answerLikeTheFallback(page);

  await page.goto('/today');
  await expect(page.getByRole('heading', { name: 'Today', exact: true })).toBeVisible();
  const notice = page.getByRole('status', { name: 'Notice from Ember' });
  await expect(notice).toContainText(noticeText);
  await expect(page.getByText('Something went wrong on our side')).toHaveCount(0);

  await page.getByRole('link', { name: 'I need a moment' }).click();
  await expect(page).toHaveURL(/\/moment$/);
  await expect(
    page.getByRole('heading', { name: "You're here. That's enough for now." }),
  ).toBeVisible();

  await page.goBack();
  await notice.getByRole('button', { name: 'Dismiss notice' }).click();
  await expect(notice).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Today', exact: true })).toBeVisible();
  await expect(page.getByText(noticeText)).toHaveCount(0);
});

test('the "can\'t be reached" sign-in state shows the maintenance notice even after it was dismissed', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('ember.dismissedNotices', JSON.stringify(['rest-synthetic'])));
  await answerLikeTheFallback(page);

  await page.goto('/today');
  await expect(page.getByRole('heading', { name: "Ember can't be reached right now" })).toBeVisible();
  await expect(page.getByRole('status')).toContainText(noticeText);
});
