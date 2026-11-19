import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { createWebUser } from './support/accounts';

interface Person {
  context: BrowserContext;
  page: Page;
  name: string;
}

const run = Date.now().toString(36);
const names = { a: `Ada${run}`, b: `Bo${run}`, c: `Cy${run}`, bRenamed: `Bea${run}` };

test.describe.serial('Fireside', () => {
  let a: Person;
  let b: Person;
  let c: Person;

  async function person(browser: Browser, name: string): Promise<Person> {
    const context = await browser.newContext();
    const page = await context.newPage();
    await createWebUser(page);
    return { context, page, name };
  }

  const nav = (page: Page) =>
    page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Fireside', exact: true });
  const friend = (page: Page, name: string) =>
    page.getByTestId('fireside-friend').filter({ hasText: name });

  async function turnOn(who: Person): Promise<void> {
    const { page } = who;
    await page.goto('/settings/people');
    await expect(nav(page)).toHaveCount(0);
    await page.getByLabel('Your name at the fireside').fill(who.name);
    await page.locator('#fireside-enabled').click();
    await page.getByRole('button', { name: 'Agree and turn on' }).click();
    await expect(page.locator('#fireside-enabled')).toBeChecked();
    await expect(nav(page)).toBeVisible();
  }

  async function makeCode(who: Person): Promise<string> {
    await who.page.goto('/fireside');
    await who.page.getByRole('button', { name: /^(Make a code|New code)$/ }).click();
    const code = who.page.getByTestId('fireside-code');
    await expect(code).toBeVisible();
    return (await code.innerText()).trim();
  }

  async function join(who: Person, code: string): Promise<void> {
    await who.page.goto('/fireside');
    await who.page.getByLabel('Got a code from someone?').fill(code.toLowerCase());
    await who.page.getByRole('button', { name: 'Join their fireside' }).click();
  }

  test.beforeAll(async ({ browser }) => {
    a = await person(browser, names.a);
    b = await person(browser, names.b);
    c = await person(browser, names.c);
  });

  test.afterAll(async () => {
    await Promise.all([a, b, c].map((who) => who?.context.close()));
  });

  test('Fireside is off until someone turns it on, and shows in the nav only then', async () => {
    await turnOn(a);
    await turnOn(b);
    await turnOn(c);
  });

  test('a code joins two people, and the code owner sees and clears the joined notice', async () => {
    const code = await makeCode(a);
    expect(code).toMatch(/^[A-Z0-9]{4} [A-Z0-9]{4}$/);
    await join(b, code);
    await expect(b.page.getByTestId('fireside-add').getByText(`You're at ${names.a}'s fireside`)).toBeVisible();
    await expect(friend(b.page, names.a)).toBeVisible();

    await a.page.reload();
    const notice = a.page.getByTestId('fireside-joined-notice');
    await expect(notice).toContainText(`${names.b} joined your fireside`);
    await notice.getByRole('button', { name: 'Okay' }).click();
    await expect(notice).toHaveCount(0);
    await expect(friend(a.page, names.b)).toBeVisible();
  });

  test('warmth is sent once a day, arrives on Today and is cleared with Okay', async () => {
    await a.page.goto('/fireside');
    await a.page.getByRole('button', { name: `Send warmth to ${names.b}` }).click();
    const sheet = a.page.getByTestId('fireside-send-sheet');
    await sheet.getByRole('radio', { name: 'Proud of you' }).click();
    await sheet.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(sheet.getByText(`“Proud of you” is on its way to ${names.b}.`)).toBeVisible();
    await sheet.getByRole('button', { name: 'Done' }).click();
    await expect(friend(a.page, names.b)).toContainText('Sent today');

    await b.page.goto('/today');
    const card = b.page.getByTestId('fireside-warmth');
    await expect(card).toContainText(`${names.a} sent you warmth`);
    await expect(card).toContainText('Proud of you');
    await card.getByRole('button', { name: `Okay, from ${names.a}` }).click();
    await expect(card).toHaveCount(0);
    await b.page.reload();
    await expect(b.page.getByTestId('fireside-ask-today')).toBeVisible();
    await expect(b.page.getByTestId('fireside-warmth')).toHaveCount(0);
  });

  test('asking for warmth lets a friend send one more, then the day is full again', async () => {
    await a.page.reload();
    await expect(friend(a.page, names.b)).toContainText('Sent today');
    await expect(a.page.getByRole('button', { name: `Send warmth to ${names.b}` })).toHaveCount(0);

    await b.page.goto('/today');
    await b.page.locator('#fireside-asking').click();
    await expect(b.page.locator('#fireside-asking')).toBeChecked();

    await a.page.reload();
    await expect(friend(a.page, names.b)).toContainText('Could use some warmth today');
    await a.page.getByRole('button', { name: `Send warmth to ${names.b}` }).click();
    const sheet = a.page.getByTestId('fireside-send-sheet');
    await sheet.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(sheet.getByRole('heading', { name: 'Sent' })).toBeVisible();
    await sheet.getByRole('button', { name: 'Done' }).click();
    await expect(friend(a.page, names.b)).toContainText('Sent today');
  });

  test('a friend who renames is announced with a notice that Okay clears', async () => {
    await b.page.goto('/settings/people');
    await b.page.getByLabel('Your name at the fireside').fill(names.bRenamed);
    await b.page.getByRole('button', { name: 'Save name' }).click();
    await expect(b.page.getByRole('button', { name: 'Save name' })).toHaveCount(0);
    b.name = names.bRenamed;

    await a.page.goto('/fireside');
    const notice = a.page.getByTestId('fireside-rename-notice');
    await expect(notice).toContainText(`${names.b} has changed their name to ${names.bRenamed}`);
    await notice.getByRole('button', { name: 'Okay' }).click();
    await expect(notice).toHaveCount(0);
    await expect(friend(a.page, names.bRenamed)).toBeVisible();
  });

  test('"Not someone I know" removes the newcomer and turns the code off', async () => {
    const code = await makeCode(a);
    await join(c, code);
    await expect(c.page.getByTestId('fireside-add').getByText(`You're at ${names.a}'s fireside`)).toBeVisible();

    await a.page.reload();
    const notice = a.page.getByTestId('fireside-joined-notice');
    await expect(notice).toContainText(`${names.c} joined your fireside`);
    await notice.getByRole('button', { name: 'Not someone I know' }).click();
    await expect(notice).toHaveCount(0);
    await expect(friend(a.page, names.c)).toHaveCount(0);
    await expect(a.page.getByTestId('fireside-code')).toHaveCount(0);
    await expect(a.page.getByText('No code to show right now.')).toBeVisible();

    await c.page.goto('/fireside');
    await expect(friend(c.page, names.a)).toHaveCount(0);
  });

  test('turning Fireside off leaves every fireside and removes the nav item', async () => {
    await b.page.goto('/settings/people');
    await b.page.locator('#fireside-enabled').click();
    await expect(b.page.getByText('You leave every fireside, and your Fireside consent ends.')).toBeVisible();
    await b.page.getByRole('button', { name: 'Turn Fireside off' }).click();
    await expect(b.page.locator('#fireside-enabled')).not.toBeChecked();
    await expect(nav(b.page)).toHaveCount(0);

    await a.page.goto('/fireside');
    await expect(friend(a.page, names.bRenamed)).toHaveCount(0);
    await expect(a.page.getByText(names.bRenamed)).toHaveCount(0);
  });
});
