import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppShell } from '@/components/app-shell';
import type { Fireside } from '@/lib/fireside';
import { auth, localMode } from '@/lib/services';
import { me, mockFetch } from '@/lib/test-support';

const base: Fireside = { enabled: true, name: 'Mara', notify: false, askingUntil: null, code: null, friends: [], warmth: [], consentVersion: '2026-11', consentGiven: true };

function renderShell() {
  const router = createMemoryRouter([
    { path: '/', Component: AppShell, children: [{ index: true, element: <h1>Today</h1> }] },
  ]);
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return screen.findByRole('heading', { name: 'Today' });
}

const navLinks = () =>
  within(screen.getByRole('navigation', { name: 'Main' }))
    .getAllByRole('link')
    .map((link) => link.textContent);

describe('main nav Fireside entry', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
    localMode.mode.set('account');
  });

  it('shows Fireside right after Today while it is on', async () => {
    auth.setMe(me);
    const fetch = mockFetch();
    await renderShell();
    (await fetch.next('GET', '/api/fireside')).respond(200, base);

    expect(await screen.findByRole('link', { name: 'Fireside' })).toHaveAttribute('href', '/fireside');
    expect(navLinks()).toEqual(['Today', 'Fireside', 'History', 'Settings']);
  });

  it('leaves Fireside out while it is off', async () => {
    auth.setMe(me);
    const fetch = mockFetch();
    await renderShell();
    (await fetch.next('GET', '/api/fireside')).respond(200, { ...base, enabled: false, name: null });

    await vi.waitFor(() => expect(fetch.pending.filter((c) => c.url === '/api/fireside')).toEqual([]));
    expect(navLinks()).toEqual(['Today', 'History', 'Settings']);
  });

  it('makes no Fireside request when signed out or in no-account mode', async () => {
    const fetch = mockFetch();
    await renderShell();
    expect(navLinks()).toEqual(['Today', 'History', 'Settings']);

    auth.setMe(me);
    localMode.mode.set('local');
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(fetch.all.filter((c) => c.url.startsWith('/api/fireside'))).toEqual([]);
    expect(navLinks()).toEqual(['Today', 'History', 'Settings']);
  });
});
