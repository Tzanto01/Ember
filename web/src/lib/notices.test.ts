import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  activeNotice,
  type LiveNotice,
  noticeText,
  parseNotices,
  refreshNotices,
  startNotices,
  liveNotices,
} from './notices';
import { mockFetch } from '@/test/test-support';

const now = new Date('2026-11-18T12:00:00Z');
const info: LiveNotice = { id: 'info-1', kind: 'info', en: 'New tips are here.' };
const maintenance: LiveNotice = {
  id: 'rest-1',
  kind: 'maintenance',
  en: 'Ember is resting until 14:00.',
  de: 'Ember ruht bis 14 Uhr.',
};

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('parseNotices', () => {
  it('keeps valid entries in order, drops broken ones and ignores unknown fields', () => {
    const body = {
      notices: [
        { ...maintenance, showUntil: '2026-11-18T14:00:00Z', priority: 3 },
        { id: 'no-text', kind: 'info' },
        { id: 'odd-kind', kind: 'alarm', en: 'Hi' },
        { id: 'bad-date', kind: 'info', en: 'Hi', showFrom: 'soon' },
        info,
      ],
    };
    expect(parseNotices(body)).toEqual([
      { ...maintenance, showUntil: '2026-11-18T14:00:00Z' },
      info,
    ]);
  });

  it('reads a null window bound as no bound', () => {
    expect(parseNotices({ notices: [{ ...info, showFrom: null, showUntil: null }] })).toEqual([info]);
  });

  it('gives nothing for a missing or wrongly shaped file', () => {
    for (const body of [null, 'oops', [], { notices: 'x' }, {}])
      expect(parseNotices(body)).toEqual([]);
  });
});

describe('activeNotice', () => {
  it('shows a notice only inside its optional window', () => {
    const later = { ...info, id: 'later', showFrom: '2026-11-18T13:00:00Z' };
    const over = { ...info, id: 'over', showUntil: '2026-11-18T11:00:00Z' };
    const open = {
      ...info,
      id: 'open',
      showFrom: '2026-11-18T11:00:00Z',
      showUntil: '2026-11-18T13:00:00Z',
    };

    expect(activeNotice([later, over, open], { now })?.id).toBe('open');
    expect(activeNotice([later, over], { now })).toBeNull();
    expect(activeNotice([later], { now: new Date('2026-11-18T13:00:00Z') })?.id).toBe('later');
  });

  it('skips dismissed notices, while the maintenance lookup for "can\'t be reached" ignores dismissal', () => {
    const notices = [maintenance, info];

    expect(activeNotice(notices, { now, dismissed: ['rest-1'] })?.id).toBe('info-1');
    expect(activeNotice(notices, { now, dismissed: ['rest-1', 'info-1'] })).toBeNull();
    expect(activeNotice(notices, { now, kind: 'maintenance' })?.id).toBe('rest-1');
    expect(activeNotice([info], { now, kind: 'maintenance' })).toBeNull();
  });
});

describe('noticeText', () => {
  it('uses the language when translated, else English, and shows at most 280 characters', () => {
    expect(noticeText(maintenance, 'de')).toBe('Ember ruht bis 14 Uhr.');
    expect(noticeText(info, 'de')).toBe('New tips are here.');
    expect(noticeText({ ...info, en: 'a'.repeat(300) }, 'en')).toHaveLength(280);
  });
});

describe('refreshNotices', () => {
  it('loads the notices, then shows nothing once the file fails to load', async () => {
    const mock = mockFetch();

    const first = refreshNotices();
    (await mock.next('GET', '/notices.json')).respond(200, { notices: [info] });
    await first;
    expect(liveNotices.get()).toEqual([info]);

    const second = refreshNotices();
    (await mock.next('GET', '/notices.json')).fail();
    await second;
    expect(liveNotices.get()).toEqual([]);
  });
});

describe('startNotices', () => {
  it('asks again when the web app comes back to the foreground, at most every 30 seconds', async () => {
    vi.useFakeTimers({ now: new Date('2026-11-18T12:00:00Z'), toFake: ['Date'] });
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    const mock = mockFetch();
    const shown = () => document.dispatchEvent(new Event('visibilitychange'));

    startNotices(false);
    (await mock.next('GET', '/notices.json')).respond(200, { notices: [info] });
    await vi.waitFor(() => expect(liveNotices.get()).toEqual([info]));

    vi.setSystemTime(new Date('2026-11-18T12:00:20Z'));
    shown();
    expect(mock.pending).toEqual([]);

    vi.setSystemTime(new Date('2026-11-18T12:00:31Z'));
    shown();
    (await mock.next('GET', '/notices.json')).respond(200, { notices: [] });
    await vi.waitFor(() => expect(liveNotices.get()).toEqual([]));
    vi.useRealTimers();
  });
});

describe('dismissed notices', () => {
  it('stay dismissed after the app restarts', async () => {
    const first = await import('./notices');
    first.dismissNotice('rest-1');

    vi.resetModules();
    const restarted = await import('./notices');
    expect(restarted.dismissedNotices.get()).toEqual(['rest-1']);
    expect(
      restarted.activeNotice([maintenance, info], {
        now,
        dismissed: restarted.dismissedNotices.get(),
      })?.id,
    ).toBe('info-1');
  });
});
