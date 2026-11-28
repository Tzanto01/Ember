import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TimeZoneSelect } from '@/components/settings/time-zone-select';
import { env } from '@/lib/env';
import { me } from '@/test/test-support';
import {
  openPatches,
  openSettings,
  resetSettings,
  settle,
  stubSettingsMedia,
} from '@/test/settings-page';

vi.mock('@/lib/env', async (original) => ({
  env: { ...(await original<typeof import('@/lib/env')>()).env },
}));
const device = vi.hoisted(() => ({ zone: 'Europe/Berlin', labelled: [] as string[] }));
vi.mock('@/lib/local-date', async (original) => {
  const actual = await original<typeof import('@/lib/local-date')>();
  return {
    ...actual,
    browserTimeZone: () => device.zone,
    timeZoneLabel: (zone: string) => {
      device.labelled.push(zone);
      return actual.timeZoneLabel(zone);
    },
  };
});
vi.mock('@/lib/services', async (original) => {
  const { Store } = await import('@/lib/store');
  return {
    ...(await original<typeof import('@/lib/services')>()),
    localMode: { mode: new Store('account'), evicted: new Store(false) },
  };
});

const zones = [
  'America/Chicago',
  'America/New_York',
  'Antarctica/Vostok',
  'Asia/Tokyo',
  'Atlantic/Bermuda',
  'Europe/Berlin',
  'Europe/Paris',
  'Europe/Vienna',
  'Europe/Zurich',
  'Pacific/Auckland',
];

/** What the list shows: `zones`, and UTC, which V8 leaves out of `Intl.supportedValuesOf`. */
const listed = zones.length + 1;

function zoneField() {
  return screen.getByRole('combobox', { name: 'Choose a timezone' });
}

function activeOption() {
  return document.getElementById(zoneField().getAttribute('aria-activedescendant') ?? '');
}

describe('TimezoneCard', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
    device.zone = 'Europe/Berlin';
    device.labelled = [];
    vi.spyOn(Intl, 'supportedValuesOf').mockReturnValue(zones);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView;
    resetSettings();
  });

  it.each([
    ['Tab', (user: ReturnType<typeof userEvent.setup>) => user.tab()],
    ['a tap elsewhere', (user: ReturnType<typeof userEvent.setup>) => user.click(document.body)],
  ])('puts the zone in use back and closes the list on %s, saving nothing', async (_, leave) => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/language');
    const field = zoneField();

    await user.clear(field);
    await user.type(field, 'tok');
    expect(screen.getByRole('listbox')).toBeInTheDocument();
    await leave(user);

    expect(field).not.toHaveFocus();
    expect(field).toHaveValue('Europe/Berlin');
    expect(screen.queryByRole('listbox')).toBeNull();
    await settle();
    expect(openPatches(fetch)).toHaveLength(0);
  });

  it('keeps the field focused when the list itself or a group heading is pressed', async () => {
    device.zone = 'America/New_York';
    const user = userEvent.setup();
    await openSettings('/settings/language');

    await user.click(zoneField());

    expect(fireEvent.mouseDown(screen.getByRole('listbox'))).toBe(false);
    expect(fireEvent.mouseDown(screen.getByText('Your browser says America/New York'))).toBe(false);
  });

  it('scrolls the opened list into view, clear of the tab bar', async () => {
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;
    const user = userEvent.setup();
    await openSettings('/settings/language');

    await user.click(zoneField());

    expect(scrollIntoView.mock.contexts).toContain(screen.getByRole('listbox'));
    expect(scrollIntoView).toHaveBeenCalledWith({ block: 'nearest' });
  });

  it('ignores Enter while a word is still being composed', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/language');
    const field = zoneField();

    await user.clear(field);
    await user.type(field, 'tokyo');
    fireEvent.keyDown(field, { key: 'Enter', isComposing: true });

    expect(screen.getByRole('listbox')).toBeInTheDocument();
    await settle();
    expect(openPatches(fetch)).toHaveLength(0);
  });

  it('drops a pending result announcement when the field goes away', () => {
    vi.useFakeTimers();
    const { unmount } = render(
      <TimeZoneSelect id="zone" value="Europe/Berlin" onChoose={() => undefined} />,
    );
    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'tok' } });
    expect(vi.getTimerCount()).toBe(1);

    unmount();

    expect(vi.getTimerCount()).toBe(0);
  });

  it('opens the list at the zone in use, moves with the arrow keys, leaves Home to the text, and Escape closes it unchanged', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/language');
    const field = zoneField();
    expect(field).toHaveValue('Europe/Berlin');
    expect(field).toHaveAccessibleDescription(/^Type a city, like Berlin or Munich/);
    expect(field).toHaveAttribute('aria-expanded', 'false');
    expect(field).not.toHaveAttribute('aria-controls');
    expect(screen.queryByRole('button', { name: /Save/ })).toBeNull();

    field.focus();
    await user.keyboard('{ArrowDown}');

    const list = screen.getByRole('listbox', { name: 'Timezones' });
    expect(field).toHaveAttribute('aria-expanded', 'true');
    expect(field).toHaveAttribute('aria-controls', list.id);
    expect(within(list).getAllByRole('option')).toHaveLength(listed);
    expect(activeOption()).toHaveTextContent(/^Europe\/Berlin \(UTC\+0[12]:00\)in use$/);
    expect(activeOption()).toHaveAttribute('aria-selected', 'true');

    await user.keyboard('{ArrowDown}');
    expect(activeOption()).toHaveTextContent(/^Europe\/Paris/);
    await user.keyboard('{ArrowUp}{ArrowUp}');
    expect(activeOption()).toHaveTextContent(/^Atlantic\/Bermuda/);
    await user.keyboard('{ArrowDown>12/}');
    expect(activeOption()).toHaveTextContent(/^UTC/);
    await user.keyboard('{Home}');
    expect(activeOption()).toHaveTextContent(/^UTC/);
    expect(field).toHaveProperty('selectionStart', 0);

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(field).toHaveAttribute('aria-expanded', 'false');
    expect(field).not.toHaveAttribute('aria-activedescendant');
    expect(field).toHaveValue('Europe/Berlin');
    await settle();
    expect(openPatches(fetch)).toHaveLength(0);
  });

  it('filters as you type, says how many match, and saves the zone Enter picks', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/language');
    const field = zoneField();

    await user.clear(field);
    await user.type(field, 'new york');

    expect(screen.getAllByRole('option').map((option) => option.textContent)).toEqual([
      expect.stringMatching(/^America\/New York \(UTC-0[45]:00\)$/),
    ]);
    expect(await screen.findByText('1 timezone found')).toHaveAttribute('role', 'status');

    await user.keyboard('{Enter}');

    expect(field).toHaveValue('America/New York');
    expect(screen.queryByRole('listbox')).toBeNull();
    const sent = await fetch.next('PATCH', '/api/me');
    expect(sent.body).toEqual({ timezone: 'America/New_York' });
    sent.respond(200, { ...me, timezone: 'America/New_York' });
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect(field).toHaveValue('America/New York');
  });

  it('keeps the list open with a gentle "no match" when nothing matches, and Enter picks nothing', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/language');
    const field = zoneField();

    await user.clear(field);
    await user.type(field, 'tokyox');
    await user.keyboard('{Enter}');

    expect(screen.queryByRole('listbox')).toBeNull();
    expect(field).toHaveAttribute('aria-expanded', 'false');
    expect(field).not.toHaveAttribute('aria-controls');
    expect(
      screen.getByText('No timezone matches that. Try a city or region, like Berlin.'),
    ).toBeVisible();
    await waitFor(() =>
      expect(
        screen.getAllByText('No timezone matches that. Try a city or region, like Berlin.'),
      ).toHaveLength(2),
    );
    expect(openPatches(fetch)).toHaveLength(0);

    await user.keyboard('{Backspace}');
    expect(screen.getAllByRole('option').map((option) => option.textContent)).toEqual([
      expect.stringMatching(/^Asia\/Tokyo/),
    ]);
  });

  it('saves a zone picked with a tap and shows the zone in use again when that save fails', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/language');
    const field = zoneField();

    await user.click(field);
    await user.click(screen.getByRole('option', { name: /^Asia\/Tokyo/ }));

    expect(field).toHaveValue('Asia/Tokyo');
    const sent = await fetch.next('PATCH', '/api/me');
    expect(sent.body).toEqual({ timezone: 'Asia/Tokyo' });
    sent.respond(500, { type: 'internal', title: 'Server error', status: 500 });

    await waitFor(() => expect(field).toHaveValue('Europe/Berlin'));
    expect(
      await screen.findByText('Something went wrong on our side. Please try again in a moment.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('Saved.')).toBeNull();
  });

  it('names the zones with their offsets only once the list opens, and only once each', async () => {
    const user = userEvent.setup();
    await openSettings('/settings/language');
    expect(device.labelled.filter((zone) => zone !== me.timezone)).toEqual([]);

    await user.click(zoneField());
    await user.keyboard('{Escape}');
    const named = device.labelled.length;
    await user.click(zoneField());

    expect(screen.getAllByRole('option')).toHaveLength(listed);
    expect(device.labelled.length).toBe(named);
  });

  it('lists the saved zone even when this device does not name it', async () => {
    const user = userEvent.setup();
    await openSettings('/settings/language', { ...me, timezone: 'America/Halifax' });

    await user.click(zoneField());

    expect(screen.getAllByRole('option')).toHaveLength(listed + 1);
    expect(activeOption()).toHaveTextContent(/^America\/Halifax \(UTC-0[34]:00\)in use$/);
  });

  it('offers UTC, which the device list leaves out, and finds it by name, not by any offset', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/language');
    const field = zoneField();

    await user.clear(field);
    await user.type(field, 'utc');

    expect(screen.getAllByRole('option').map((option) => option.textContent)).toEqual([
      'UTC (UTC+00:00)',
    ]);
    await user.keyboard('{Enter}');

    expect((await fetch.next('PATCH', '/api/me')).body).toEqual({ timezone: 'UTC' });
  });

  it.each([
    ['ber', ['Europe/Berlin', 'Atlantic/Bermuda', 'Europe/Zurich']],
    ['tok', ['Asia/Tokyo', 'Antarctica/Vostok']],
    ['München', ['Europe/Berlin']],
    ['Munich', ['Europe/Berlin']],
    ['hamburg', ['Europe/Berlin']],
    ['Wien', ['Europe/Vienna']],
    ['Zürich', ['Europe/Zurich']],
    ['zurich', ['Europe/Zurich']],
  ])('typing %s lists cities that start with it first', async (typed, expected) => {
    const user = userEvent.setup();
    await openSettings('/settings/language');
    const field = zoneField();

    await user.clear(field);
    await user.type(field, typed);

    expect(
      screen.getAllByRole('option').map((option) => option.textContent?.replace(/ \(UTC.*$/, '')),
    ).toEqual(expected);
    expect(activeOption()).toHaveTextContent(new RegExp(`^${expected[0]}`));
  });

  it.each([
    [false, 'Your browser says America/New York'],
    [true, 'Your phone says America/New York'],
  ])(
    'offers this device zone first (native app: %s) and saves it when picked',
    async (native, label) => {
      env.native = native;
      device.zone = 'America/New_York';
      const user = userEvent.setup();
      const { fetch } = await openSettings('/settings/language');

      await user.click(zoneField());

      const options = screen.getAllByRole('option');
      expect(options[0]).toHaveTextContent(/^America\/New York/);
      expect(within(screen.getByRole('group', { name: label })).getAllByRole('option')).toEqual([
        options[0],
      ]);
      expect(
        within(screen.getByRole('group', { name: 'All timezones' })).getAllByRole('option'),
      ).toHaveLength(listed - 1);

      await user.click(options[0] as HTMLElement);

      expect((await fetch.next('PATCH', '/api/me')).body).toEqual({ timezone: 'America/New_York' });
    },
  );

  it('before a zone is picked, offers this device zone even when Ember already uses it, and saves it', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/language', { ...me, timezoneSet: false });
    expect(screen.getByText(/You haven't picked a timezone yet/)).toBeInTheDocument();

    await user.click(zoneField());
    await user.click(
      within(screen.getByRole('group', { name: 'Your browser says Europe/Berlin' })).getByRole(
        'option',
      ),
    );

    expect((await fetch.next('PATCH', '/api/me')).body).toEqual({ timezone: 'Europe/Berlin' });
  });
});
