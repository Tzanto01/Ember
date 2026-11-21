import { describe, expect, it } from 'vitest';
import type { DayEntry, DayNote, DayStat, OwnMood } from '@/lib/models';
import {
  daysFromEntries,
  daysFromStats,
  showedUpPerYear,
  showedUpRecently,
  yearStart,
} from './year-data';

const stat = (
  date: string,
  scheduled: number,
  done: number,
  partial = 0,
  showedUp = done + partial > 0,
): DayStat => ({
  date,
  scheduled,
  done,
  partial,
  showedUp,
});

const entry = (date: string, state: DayEntry['state']): DayEntry => ({
  date,
  state,
  completed: state === 'done' ? true : null,
  count: state === 'done' ? 1 : 0,
  skipped: false,
});

const noMoods: OwnMood[] = [];

describe('daysFromStats', () => {
  it.each([
    [3, 3, 3],
    [2, 3, 2],
    [3, 6, 2],
    [1, 3, 1],
  ] as const)('fills a day with %i of %i done as tone %i', (done, scheduled, tone) => {
    const days = daysFromStats([stat('2026-11-01', scheduled, done)], [], noMoods);

    expect(days.get('2026-11-01')).toMatchObject({ tone, showedUp: true });
  });

  it('words a day by what was done, mentioning partly done check-ins only when there are some', () => {
    const days = daysFromStats(
      [stat('2026-11-01', 3, 2), stat('2026-11-02', 3, 1, 1)],
      [],
      noMoods,
    );

    expect(days.get('2026-11-01')?.words).toBe('you showed up, 2 of 3 done');
    expect(days.get('2026-11-02')?.words).toBe('you showed up, 1 of 3 done, 1 partly');
  });

  it('counts a check-in on a day with nothing scheduled as showing up at the middle tone', () => {
    const days = daysFromStats([stat('2026-11-01', 0, 0, 0, true)], [], noMoods);

    expect(days.get('2026-11-01')).toMatchObject({
      tone: 2,
      showedUp: true,
      words: 'you showed up',
    });
  });

  it('leaves a day without check-ins empty, in gentle words, never "missed"', () => {
    const days = daysFromStats([stat('2026-11-01', 2, 0), stat('2026-11-02', 0, 0)], [], noMoods);

    expect(days.get('2026-11-01')).toMatchObject({
      tone: 0,
      showedUp: false,
      words: 'no check-ins',
    });
    expect(days.get('2026-11-02')).toMatchObject({
      tone: 0,
      showedUp: false,
      words: 'nothing planned',
    });
  });

  it('adds the mood of a day to its cell and makes a mood-only day a cell of its own', () => {
    const own: OwnMood[] = [{ id: 4, label: 'Wired', emoji: '⚡', archived: false }];
    const notes: DayNote[] = [
      { date: '2026-11-01', text: null, mood: 'good' },
      { date: '2026-10-31', text: null, mood: 'custom:4' },
      { date: '2026-10-30', text: 'Only words', mood: null },
    ];

    const days = daysFromStats([stat('2026-11-01', 1, 1)], notes, own);

    expect(days.get('2026-11-01')?.mood).toMatchObject({ key: 'good', emoji: '🌤️' });
    expect(days.get('2026-10-31')).toMatchObject({
      tone: 0,
      showedUp: false,
      words: 'nothing recorded',
      mood: { key: 'custom:4', label: 'Wired', emoji: '⚡' },
    });
    expect(days.has('2026-10-30')).toBe(false);
  });
});

describe('daysFromEntries', () => {
  it('maps day states to tones and says only done and partly done days showed up', () => {
    const states = ['done', 'partial', 'lowday', 'paused', 'offday', 'notdone', 'pending'] as const;

    const days = daysFromEntries(
      states.map((state, i) => entry(`2026-10-0${i + 1}`, state)),
      [],
      noMoods,
    );

    expect([...days.values()].map((d) => [d.tone, d.showedUp])).toEqual([
      [3, true],
      [2, true],
      ['lowday', false],
      ['paused', false],
      ['offday', false],
      [0, false],
      [0, false],
    ]);
    expect(days.get('2026-10-01')?.words).toBe('done');
    expect(days.get('2026-10-06')?.words).toBe('not checked in');
  });

  it('leaves out days before the habit started, but keeps a mood recorded on one', () => {
    const notes: DayNote[] = [{ date: '2026-10-01', text: null, mood: 'low' }];

    const days = daysFromEntries(
      [entry('2026-10-01', 'beforestart'), entry('2026-10-02', 'done')],
      notes,
      noMoods,
    );

    expect([...days.keys()]).toEqual(['2026-10-02', '2026-10-01']);
    expect(days.get('2026-10-01')).toMatchObject({
      tone: 0,
      showedUp: false,
      words: 'nothing recorded',
    });
  });
});

describe('showedUpRecently', () => {
  const days = daysFromStats(
    [
      stat('2026-11-01', 1, 1),
      stat('2026-10-03', 1, 1),
      stat('2026-10-02', 1, 1),
      stat('2026-10-30', 1, 0),
    ],
    [],
    noMoods,
  );

  it('looks at the 30 days ending today, today included', () => {
    expect(showedUpRecently(days, '2026-11-01', '2025-01-01')).toEqual({ showed: 2, of: 30 });
  });

  it('counts only days from the first one on', () => {
    expect(showedUpRecently(days, '2026-11-01', '2026-10-30')).toEqual({ showed: 1, of: 3 });
  });

  it('takes a window of another length', () => {
    expect(showedUpRecently(days, '2026-11-01', '2025-01-01', 7)).toEqual({ showed: 1, of: 7 });
  });
});

describe('showedUpPerYear', () => {
  it('counts the days showed up per calendar year, newest year first, leaving out years without any', () => {
    const days = daysFromStats(
      [
        stat('2026-01-02', 1, 1),
        stat('2026-11-01', 1, 1),
        stat('2025-12-31', 1, 1),
        stat('2024-06-01', 1, 0),
      ],
      [],
      noMoods,
    );

    expect(showedUpPerYear(days)).toEqual([
      { year: 2026, days: 2 },
      { year: 2025, days: 1 },
    ]);
  });
});

describe('yearStart', () => {
  it.each([
    ['2026-11-01', '2025-11-03'],
    ['2026-11-02', '2025-11-10'],
    ['2026-11-04', '2025-11-10'],
  ])('starts the grid on the Monday 51 weeks before the week of %s', (today, start) => {
    expect(yearStart(today)).toBe(start);
  });
});
