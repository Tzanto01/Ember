import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { env } from '@/lib/env';
import { limits } from '@/lib/local/limits';
import { applyCalm } from '@/lib/theme';
import { inGerman } from '@/test/test-utils';
import { Guide } from './guide';

vi.mock('@/lib/env', async (original) => ({ env: { ...(await original<typeof import('@/lib/env')>()).env } }));
const back = vi.hoisted(() => ({ handler: null as (() => void) | null, removed: 0 }));
vi.mock('@capacitor/app', () => ({
  App: {
    addListener: async (_event: string, handler: () => void) => {
      back.handler = handler;
      return { remove: async () => { back.removed++; } };
    },
  },
}));

function Page({ lowDayButton = true }: { lowDayButton?: boolean }) {
  return (
    <>
      <Guide />
      <main id="main">
        <h1>Page</h1>
        <button type="button" data-guide="check-in" aria-pressed="true">
          Check in Read for today
        </button>
        <button type="button" data-guide="check-in" aria-pressed="false">
          Check in Walk for today
        </button>
        {lowDayButton && (
          <button type="button" id="mark-low-day">
            Mark today as a low day
          </button>
        )}
      </main>
    </>
  );
}

function renderAt(url: string, page = <Page />) {
  const router = createMemoryRouter([{ path: '*', element: page }], { initialEntries: [url] });
  render(<RouterProvider router={router} />);
  return router;
}

const guideButton = () => screen.getByRole('button', { name: 'How this works' });

async function openQuestion(name: string) {
  await userEvent.click(guideButton());
  await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name }));
}

function stubWide(wide: boolean) {
  window.matchMedia = (query: string) =>
    ({ matches: wide && query === '(min-width: 64rem)', addEventListener: () => undefined, removeEventListener: () => undefined }) as unknown as MediaQueryList;
}

describe('Guide', () => {
  let scrolled: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    stubWide(false);
    scrolled = vi.fn();
    Element.prototype.scrollIntoView = scrolled as unknown as Element['scrollIntoView'];
  });

  afterEach(() => {
    applyCalm(false);
    localStorage.clear();
    env.native = false;
  });

  it('offers "How this works" on screens with a guide and nothing on the others', async () => {
    const router = renderAt('/today');
    expect(guideButton()).toBeInTheDocument();

    await act(() => router.navigate('/history/year'));
    expect(screen.queryByRole('button', { name: 'How this works' })).toBeNull();
  });

  it("opens the screen's guide with its questions, one answer open at a time", async () => {
    renderAt('/today');
    await userEvent.click(guideButton());

    const dialog = screen.getByRole('dialog', { name: 'Today, explained' });
    expect(within(dialog).getAllByRole('heading', { level: 3 }).map((h) => h.textContent)).toEqual([
      'What do “1/2” and the empty circle mean?',
      'What does “2 of 3 this week” mean?',
      'What is a low day?',
      'When will Ember remind me, and can I check in ahead?',
      'What is “I need a moment”?',
    ]);

    await userEvent.click(within(dialog).getByRole('button', { name: 'What is a low day?' }));
    expect(within(dialog).getByText(/A low day is for days that are hard/)).toBeVisible();
    await userEvent.click(within(dialog).getByRole('button', { name: 'What is “I need a moment”?' }));
    expect(within(dialog).queryByText(/A low day is for days that are hard/)).toBeNull();
  });

  it('fills the numbers in from the limits Ember enforces', async () => {
    renderAt('/habits/new');
    await openQuestion('How many habits can I have, and how long can names be?');

    expect(
      screen.getByText(`You can have up to ${limits.habits} habits. A name can be up to ${limits.habitName} characters`, {
        exact: false,
      }),
    ).toBeInTheDocument();
  });

  it('closes on Escape and gives focus back to the button', async () => {
    renderAt('/today');
    await userEvent.click(guideButton());

    await userEvent.keyboard('{Escape}');

    expect(screen.queryByRole('dialog')).toBeNull();
    await waitFor(() => expect(guideButton()).toHaveFocus());
  });

  it('"Show me" closes the guide, brings the first open check-in to the middle, rings it briefly and focuses it', async () => {
    renderAt('/today');
    await openQuestion('What do “1/2” and the empty circle mean?');

    await userEvent.click(screen.getByRole('button', { name: 'Show me' }));

    expect(screen.queryByRole('dialog')).toBeNull();
    const target = screen.getByRole('button', { name: 'Check in Walk for today' });
    await waitFor(() => expect(target).toHaveFocus());
    expect(scrolled).toHaveBeenCalledWith({ block: 'center', behavior: 'smooth' });
    expect(scrolled.mock.contexts[0]).toBe(target);
    expect(target.className).toMatch(/ring-primary/);
    await waitFor(() => expect(target.className).not.toMatch(/ring-primary/), { timeout: 2500 });
  });

  it('scrolls to the target at once in calm mode', async () => {
    applyCalm(true);
    renderAt('/today');
    await openQuestion('What is a low day?');

    await userEvent.click(screen.getByRole('button', { name: 'Show me' }));

    await waitFor(() => expect(scrolled).toHaveBeenCalledWith({ block: 'center', behavior: 'auto' }));
  });

  it('leaves "Show me" out until its target is on the page', async () => {
    renderAt('/today', <Page lowDayButton={false} />);
    await openQuestion('What is a low day?');

    expect(screen.getByText(/A low day is for days that are hard/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Show me' })).toBeNull();

    const loaded = document.createElement('button');
    loaded.id = 'mark-low-day';
    document.getElementById('main')!.append(loaded);
    await userEvent.click(screen.getByRole('button', { name: 'What is “I need a moment”?' }));
    await userEvent.click(screen.getByRole('button', { name: 'What is a low day?' }));
    expect(screen.getByRole('button', { name: 'Show me' })).toBeInTheDocument();
    loaded.remove();
  });

  it('ends with a link to the feedback screen', async () => {
    renderAt('/today');
    await userEvent.click(guideButton());

    expect(screen.getByText('Still unclear?')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Send feedback' })).toHaveAttribute('href', '/feedback');
  });

  it('stays open beside the page on wide screens while the page is used', async () => {
    stubWide(true);
    renderAt('/today');
    await userEvent.click(guideButton());

    await userEvent.click(screen.getByRole('button', { name: 'Check in Walk for today' }));
    await userEvent.keyboard('{Escape}');
    expect(screen.getByRole('dialog', { name: 'Today, explained' })).toBeInTheDocument();

    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Close' }));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('lets Tab move on from the wide panel to the page', async () => {
    stubWide(true);
    renderAt('/today');
    await userEvent.click(guideButton());
    const feedback = screen.getByRole('link', { name: 'Send feedback' });
    feedback.focus();

    const tab = fireEvent.keyDown(feedback, { key: 'Tab' });

    expect(tab).toBe(true);
    expect(feedback).toHaveFocus();
  });

  it('keeps the mode it opened in when the screen narrows', async () => {
    stubWide(true);
    renderAt('/today');
    await userEvent.click(guideButton());

    stubWide(false);
    await userEvent.click(screen.getByRole('button', { name: 'Check in Walk for today' }));

    expect(screen.getByRole('dialog', { name: 'Today, explained' })).toBeInTheDocument();
  });

  it('makes room beside the page for the wide panel while it is open', async () => {
    stubWide(true);
    renderAt('/today');
    const main = document.getElementById('main')!;
    const panelWidth = vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(384);
    main.getBoundingClientRect = () => ({ right: window.innerWidth - 100 }) as DOMRect;

    await userEvent.click(guideButton());
    expect(main.style.paddingRight).toBe('calc(284px + 1.5rem)');

    await userEvent.keyboard('{Escape}');
    expect(main.style.paddingRight).toBe('');
    panelWidth.mockRestore();
  });

  it('closes the sheet on Android Back instead of leaving the screen', async () => {
    env.native = true;
    back.removed = 0;
    renderAt('/today');
    await userEvent.click(guideButton());
    await waitFor(() => expect(back.handler).not.toBeNull());

    act(() => back.handler!());

    expect(screen.queryByRole('dialog')).toBeNull();
    await waitFor(() => expect(back.removed).toBe(1));
  });

  it('speaks German in German', async () => {
    await inGerman();
    renderAt('/today');
    await userEvent.click(screen.getByRole('button', { name: "So funktioniert's" }));

    expect(screen.getByRole('dialog', { name: 'Heute erklärt' })).toBeInTheDocument();
  });
});
