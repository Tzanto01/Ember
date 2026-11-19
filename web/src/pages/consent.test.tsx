import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderPage } from '@/test-utils';
import { auth } from '@/lib/services';
import { type FetchMock, me, mockFetch } from '@/lib/test-support';
import ConsentPage from './consent';

const notGiven = { given: false, policyVersion: null, currentPolicyVersion: '1', withdrawnAt: null, deletesAt: null };
const withdrawn = {
  ...notGiven,
  policyVersion: '1',
  withdrawnAt: '2026-11-01T09:00:00Z',
  deletesAt: '2026-12-01T09:00:00Z',
};

describe('ConsentPage', () => {
  let mock: FetchMock;

  beforeEach(() => {
    mock = mockFetch();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
  });

  it('agrees to the current policy version, reloads me and lands on the returnUrl', async () => {
    auth.setMe({ ...me, consent: { ...me.consent, policyVersion: '1', currentPolicyVersion: '2' } });
    const router = renderPage('/consent', ConsentPage, '/consent?returnUrl=%2Fhistory%3Fmonth%3D2026-10');
    expect(screen.getByRole('heading', { name: 'Our privacy policy changed' })).toBeInTheDocument();
    const user = userEvent.setup();

    await user.click(screen.getByRole('checkbox', { name: "I'm at least 16" }));
    await user.click(screen.getByRole('button', { name: 'I agree' }));
    const consent = await mock.next('POST', '/api/me/consent');
    expect(consent.body).toEqual({ policyVersion: '2', ageConfirmed: true });
    consent.respond(204);
    (await mock.next('GET', '/api/me')).respond(200, {
      ...me,
      consent: { ...me.consent, policyVersion: '2', currentPolicyVersion: '2' },
    });

    await waitFor(() =>
      expect(router.state.location.pathname + router.state.location.search).toBe('/history?month=2026-10'),
    );
  });

  it('names everything the consent covers, journal entries and moods included', () => {
    auth.setMe({ ...me, consent: notGiven });
    renderPage('/consent', ConsentPage, '/consent');

    expect(screen.getByText(/your habit names, check-ins, low days, day notes, moods, slips/)).toBeInTheDocument();
  });

  it('falls back to /today for an off-site returnUrl', async () => {
    auth.setMe({ ...me, consent: notGiven });
    const router = renderPage('/consent', ConsentPage, '/consent?returnUrl=//evil.example');
    expect(screen.getByRole('heading', { name: 'One quick question' })).toBeInTheDocument();
    const user = userEvent.setup();

    await user.click(screen.getByRole('checkbox', { name: "I'm at least 16" }));
    await user.click(screen.getByRole('button', { name: 'I agree' }));
    (await mock.next('POST', '/api/me/consent')).respond(204);
    (await mock.next('GET', '/api/me')).respond(200, me);

    await waitFor(() => expect(router.state.location.pathname).toBe('/today'));
  });

  it('sends nothing until the age box is ticked, and points the error at the box', async () => {
    auth.setMe({ ...me, consent: notGiven });
    renderPage('/consent', ConsentPage, '/consent');
    const user = userEvent.setup();
    const box = screen.getByRole('checkbox', { name: "I'm at least 16" });

    await user.click(screen.getByRole('button', { name: 'I agree' }));

    expect(box).toHaveFocus();
    expect(box).toBeInvalid();
    expect(box).toHaveAccessibleDescription("Please confirm that you're at least 16.");
    expect(mock.all.filter((c) => c.method === 'POST')).toEqual([]);
  });

  it('"No thanks" declines, reloads me and lands on Settings', async () => {
    auth.setMe({ ...me, consent: notGiven });
    const router = renderPage('/consent', ConsentPage, '/consent?returnUrl=%2Ftoday');

    await userEvent.setup().click(screen.getByRole('button', { name: 'No thanks' }));
    (await mock.next('POST', '/api/me/consent/decline')).respond(204);
    (await mock.next('GET', '/api/me')).respond(200, { ...me, consent: { ...notGiven, withdrawnAt: '2026-11-19T09:00:00Z' } });

    await waitFor(() => expect(router.state.location.pathname).toBe('/settings'));
  });

  it('while withdrawn, shows the deletion date, the export and a way to delete, and agreeing again lands on Today', async () => {
    auth.setMe({ ...me, timezone: 'Europe/Berlin', consent: withdrawn });
    const router = renderPage('/consent', ConsentPage, '/consent?returnUrl=%2Fsettings');
    const user = userEvent.setup();

    expect(screen.getByRole('heading', { name: "Ember isn't saving anything right now" })).toBeInTheDocument();
    expect(screen.getByText(/Ember deletes it on December 1, 2026/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Download JSON' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Download CSV' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Delete your account instead' })).toHaveAttribute('href', '/settings/privacy#delete');
    expect(screen.queryByRole('button', { name: 'No thanks' })).toBeNull();

    await user.click(screen.getByRole('checkbox', { name: "I'm at least 16" }));
    await user.click(screen.getByRole('button', { name: 'I agree' }));
    const consent = await mock.next('POST', '/api/me/consent');
    expect(consent.body).toEqual({ policyVersion: '1', ageConfirmed: true });
    consent.respond(204);
    (await mock.next('GET', '/api/me')).respond(200, me);

    await waitFor(() => expect(router.state.location.pathname).toBe('/today'));
  });

  it('while withdrawn without data left, says so without a deletion date', () => {
    auth.setMe({ ...me, consent: { ...withdrawn, deletesAt: null } });
    renderPage('/consent', ConsentPage, '/consent');

    expect(
      screen.getByText('Without your consent, Ember saves nothing new and sends no reminders. Your account and settings stay.'),
    ).toBeInTheDocument();
  });
});
