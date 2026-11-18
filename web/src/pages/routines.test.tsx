import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Routine } from '@/lib/models';
import { type FetchMock, habit, mockFetch } from '@/lib/test-support';
import RoutinesPage from './routines';

const routine = (id: number, name: string): Routine => ({ id, name, reminderTime: null, reminderChannels: [], habitIds: [] });
const routines = [routine(1, 'Mental'), routine(2, 'Creative'), routine(3, 'Physical')];

async function renderPage(): Promise<FetchMock> {
  const f = mockFetch();
  const router = createMemoryRouter([{ path: '/routines', Component: RoutinesPage }], { initialEntries: ['/routines'] });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  (await f.next('GET', '/api/routines')).respond(200, routines);
  (await f.next('GET', '/api/habits')).respond(200, [habit]);
  await screen.findByRole('heading', { name: 'Mental' });
  return f;
}

const shownOrder = () =>
  within(screen.getAllByRole('list')[0]).getAllByRole('heading', { level: 2 }).map((h) => h.textContent);

describe('RoutinesPage order', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('moves a routine up, saves the whole order and announces the new place', async () => {
    const user = userEvent.setup();
    const f = await renderPage();

    const up = screen.getByRole('button', { name: 'Move Physical routine up' });
    await user.click(up);

    expect(shownOrder()).toEqual(['Mental', 'Physical', 'Creative']);
    expect(up).toHaveFocus();
    expect(await screen.findByText('Physical routine moved to 2 of 3.')).toBeInTheDocument();
    const save = await f.next('PUT', '/api/routines/order');
    expect(save.body).toEqual({ ids: [1, 3, 2] });
    save.respond(204);
  });

  it('puts the order back and says so when saving fails', async () => {
    const user = userEvent.setup();
    const f = await renderPage();

    await user.click(screen.getByRole('button', { name: 'Move Mental routine down' }));
    expect(shownOrder()).toEqual(['Creative', 'Mental', 'Physical']);
    (await f.next('PUT', '/api/routines/order')).respond(409, { type: 'conflict', detail: 'Your routines changed in the meantime.' });

    expect(await screen.findByText("The new order didn't save")).toBeInTheDocument();
    (await f.next('GET', '/api/routines')).respond(200, routines);
    await waitFor(() => expect(shownOrder()).toEqual(['Mental', 'Creative', 'Physical']));
  });
});
