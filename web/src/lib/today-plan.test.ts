import { describe, expect, it } from 'vitest';
import type { AnyHabit, Habit, Routine } from './models';
import { habit, letGoHabit } from '@/test/test-support';
import { moveKey, orderAfterMove, planToday, routineOrderAfterMove, widgetDay } from './today-plan';

const date = '2026-11-01';

function make(id: number, name: string, change: Partial<Habit> = {}): Habit {
  return { ...habit, id, name, ...change };
}

const weekly = (id: number, done: number, target: number, count: number) =>
  make(id, `Swim ${id}`, {
    frequency: 'weekly',
    weeklyTarget: target,
    today: {
      date,
      state: count > 0 ? 'done' : 'pending',
      completed: count > 0 ? true : null,
      count,
      skipped: false,
    },
    stats: { ...habit.stats, week: { done, target } },
  });

const routine = (id: number, habitIds: number[], reminderTime: string | null = null): Routine => ({
  id,
  name: `Routine ${id}`,
  reminderTime,
  reminderChannels: [],
  habitIds,
});

const topKeys = (habits: Habit[], routines: Routine[] = [], order: string[] | null = null) =>
  planToday(habits, routines, order, date).today.map((item) => item.key);
const laterKeys = (habits: Habit[], routines: Routine[] = []) =>
  planToday(habits, routines, null, date).notToday.map((item) => item.key);

describe('planToday sections', () => {
  it('keeps a daily habit for today whether it is open, done or on a low day', () => {
    const states = (['pending', 'done', 'lowday', 'partial'] as const).map((state, i) =>
      make(i + 1, `H${i}`, {
        today: { date, state, completed: null, count: state === 'pending' ? 0 : 1, skipped: false },
      }),
    );
    expect(topKeys(states)).toEqual(['h:1', 'h:2', 'h:3', 'h:4']);
    expect(laterKeys(states)).toEqual([]);
  });

  it('keeps a weekly habit for today through its target-reaching check-in and moves it down once the target was met before today', () => {
    const open = weekly(1, 1, 2, 0);
    const reachedToday = weekly(2, 2, 2, 1);
    const metBefore = weekly(3, 2, 2, 0);
    const metBeforeAndCheckedInAgain = weekly(4, 3, 2, 1);

    const plan = planToday(
      [open, reachedToday, metBefore, metBeforeAndCheckedInAgain],
      [],
      null,
      date,
    );

    expect(plan.today.map((i) => i.key)).toEqual(['h:1', 'h:2']);
    expect(plan.notToday).toEqual([
      { key: 'h:3', habit: metBefore, reason: 'doneForWeek' },
      { key: 'h:4', habit: metBeforeAndCheckedInAgain, reason: 'doneForWeek' },
    ]);
  });

  it('counts a weekly check-in from its count, so a low day with a check-in does not move it', () => {
    const lowDayCheckedIn = make(1, 'Swim', {
      frequency: 'weekly',
      weeklyTarget: 2,
      today: { date, state: 'lowday', completed: null, count: 1, skipped: false },
      stats: { ...habit.stats, week: { done: 2, target: 2 } },
    });
    expect(topKeys([lowDayCheckedIn])).toEqual(['h:1']);
  });

  it('gives each Not for today row its reason, from the schedule rather than the day state', () => {
    const paused = make(1, 'Read', { pausedToday: true, pausedUntil: '2026-11-03' });
    const restDays = { frequency: 'weekdays' as const, days: ['mon' as const, 'wed' as const] };
    const rest = make(2, 'Gym', restDays);
    const restCheckedIn = make(3, 'Run', {
      ...restDays,
      today: { date, state: 'done', completed: true, count: 1, skipped: false },
    });
    const restLowDay = make(4, 'Yoga', {
      ...restDays,
      today: { date, state: 'lowday', completed: null, count: 0, skipped: false },
    });
    const notStarted = make(5, 'Walk', {
      startDate: '2026-11-05',
      today: { date: '2026-10-31', state: 'beforestart', completed: null, count: 0, skipped: false },
    });
    const planned = make(6, 'Stretch', { frequency: 'weekdays', days: ['sun'] });

    const plan = planToday([paused, rest, restCheckedIn, restLowDay, notStarted, planned], [], null, date);

    expect(plan.today.map((i) => i.key)).toEqual(['h:6']);
    expect(plan.notToday.map(({ key, reason }) => [key, reason])).toEqual([
      ['h:2', 'restDay'],
      ['h:1', 'paused'],
      ['h:3', 'restDay'],
      ['h:5', 'notStarted'],
      ['h:4', 'restDay'],
    ]);
  });

  it("keeps a habit that starts later with today's habits, as before", () => {
    const later = make(1, 'Walk', {
      startDate: '2026-11-05',
      today: { date, state: 'beforestart', completed: null, count: 0, skipped: false },
    });
    expect(topKeys([later])).toEqual(['h:1']);
  });

  it('shows a routine for today with only its members that are for today, in routine order, and lists the others one by one below', () => {
    const rest = make(1, 'Gym', { frequency: 'weekdays', days: ['mon'] });
    const water = make(2, 'Water');
    const stretch = make(3, 'Stretch');

    const plan = planToday([rest, water, stretch], [routine(5, [3, 1, 2])], null, date);

    expect(plan.today).toHaveLength(1);
    expect(plan.today[0]).toMatchObject({ kind: 'routine', key: 'r:5' });
    expect(plan.today[0].kind === 'routine' && plan.today[0].habits.map((h) => h.id)).toEqual([
      3, 2,
    ]);
    expect(plan.notToday.map((i) => i.key)).toEqual(['h:1']);
  });

  it('leaves a routine out of For today when none of its members is for today', () => {
    const rest = make(1, 'Gym', { frequency: 'weekdays', days: ['mon'] });
    const plan = planToday([rest], [routine(5, [1])], null, date);
    expect(plan.today).toEqual([]);
    expect(plan.notToday.map((i) => i.key)).toEqual(['h:1']);
  });

  it('keeps let-go habits in their own section and out of the widget', () => {
    const plan = planToday([habit, letGoHabit], [], null, date);
    expect(plan.letGo).toEqual([letGoHabit]);
    expect(plan.today.map((i) => i.key)).toEqual(['h:1']);
    expect(widgetDay([habit, letGoHabit], [], null, date).map((h) => h.id)).toEqual([1]);
  });
});

describe('Today order', () => {
  const at = (id: number, name: string, time: string | null) =>
    make(id, name, { reminderTime: time, reminderTimes: time ? [time] : [] });

  it('sorts automatically by reminder time, a routine by its own, then the ones without a time by name and id', () => {
    const habits = [
      at(1, 'read', null),
      at(2, 'Walk', '17:00'),
      at(3, 'Meds', '08:00'),
      at(4, 'Read', null),
      at(5, 'Apple', null),
      at(6, 'Tea', '06:00'),
    ];
    expect(topKeys(habits, [routine(7, [6], '07:30')])).toEqual([
      'r:7',
      'h:3',
      'h:2',
      'h:5',
      'h:1',
      'h:4',
    ]);
  });

  it('uses the saved order and places a new item before the first placed item with a later time, or last without a time', () => {
    const habits = [
      at(1, 'Meds', '08:00'),
      at(2, 'Walk', '17:00'),
      at(3, 'Read', '21:00'),
      at(4, 'Yoga', '07:00'),
      at(5, 'Tea', null),
      at(6, 'Lunch walk', '12:00'),
    ];
    expect(topKeys(habits, [], ['h:1', 'h:3', 'h:2'])).toEqual([
      'h:4',
      'h:1',
      'h:6',
      'h:3',
      'h:2',
      'h:5',
    ]);
  });

  it('goes back to automatic once the order is reset', () => {
    const habits = [at(1, 'Meds', '08:00'), at(2, 'Walk', '17:00')];
    expect(topKeys(habits, [], ['h:2', 'h:1'])).toEqual(['h:2', 'h:1']);
    expect(topKeys(habits, [], null)).toEqual(['h:1', 'h:2']);
  });

  it('keeps done items in place after a check-in', () => {
    const habits = [at(1, 'Meds', '08:00'), at(2, 'Walk', '17:00')];
    const checkedIn = habits.map((h) =>
      h.id === 2
        ? { ...h, today: { ...h.today, state: 'done' as const, completed: true, count: 1, skipped: false } }
        : h,
    );
    expect(topKeys(checkedIn, [], ['h:2', 'h:1'])).toEqual(topKeys(habits, [], ['h:2', 'h:1']));
  });

  it('orders the widget day like For today, with routines flattened in member order and done items kept', () => {
    const meds = at(1, 'Meds', '08:00');
    const walk = {
      ...at(2, 'Walk', '17:00'),
      today: { date, state: 'done' as const, completed: true, count: 1, skipped: false },
    };
    const water = at(3, 'Water', null);
    const stretch = at(4, 'Stretch', null);
    const gym = make(5, 'Gym', { frequency: 'weekdays', days: ['mon'] });
    const ids = widgetDay(
      [meds, walk, water, stretch, gym],
      [routine(9, [4, 3], '08:30')],
      ['h:2', 'r:9', 'h:1'],
      date,
    ).map((h) => h.id);
    expect(ids).toEqual([2, 4, 3, 1]);
  });

  it('leaves a habit out of the widget before its start', () => {
    const later = make(1, 'Walk', {
      startDate: '2026-11-02',
      today: { date, state: 'beforestart', completed: null, count: 0, skipped: false },
    });
    expect(widgetDay([later], [], null, date)).toEqual([]);
  });

  it("plans tomorrow's weekly habits from this week's count, and from nothing in a new week", () => {
    const saturday = '2026-10-31';
    const onSaturday = (h: Habit) => ({ ...h, today: { ...h.today, date: saturday } });
    const met = weekly(2, 2, 2, 1);
    const open = weekly(3, 1, 2, 1);
    expect(widgetDay([onSaturday(met), onSaturday(open)], [], null, date).map((h) => h.id)).toEqual(
      [3],
    );
    expect(widgetDay([met, open], [], null, '2026-11-02').map((h) => h.id)).toEqual([2, 3]);
  });
});

describe('saving a new order', () => {
  it('moves a key within its list and stops at the ends', () => {
    expect(moveKey(['a', 'b', 'c'], 'c', -1)).toEqual(['a', 'c', 'b']);
    expect(moveKey(['a', 'b', 'c'], 'a', -1)).toEqual(['a', 'b', 'c']);
  });

  it('writes the full order: the moved section in its slots, other sections and unknown keys in place', () => {
    const saved = ['h:9', 'h:1', 'r:2', 'h:7', 'h:3'];
    const displayed = [['h:1', 'r:2', 'h:4', 'h:3'], ['h:7']];
    expect(orderAfterMove(saved, displayed, 0, ['h:3', 'h:1', 'r:2', 'h:4'])).toEqual([
      'h:9',
      'h:3',
      'h:1',
      'r:2',
      'h:7',
      'h:4',
    ]);
  });

  it('writes the whole shown list on the first move, with no saved order', () => {
    expect(orderAfterMove(null, [['h:1', 'h:2'], ['h:5'], ['h:3']], 0, ['h:2', 'h:1'])).toEqual([
      'h:2',
      'h:1',
      'h:5',
      'h:3',
    ]);
  });

  it("rearranges a routine's shown members and keeps a hidden member in its slot", () => {
    expect(routineOrderAfterMove([4, 1, 3, 2], [2, 4, 3])).toEqual([2, 1, 4, 3]);
  });

  it('stops a move at the end of the list and leaves the list alone for an unknown key', () => {
    expect(moveKey(['a', 'b', 'c'], 'a', 5)).toEqual(['b', 'c', 'a']);
    expect(moveKey(['a', 'b', 'c'], 'z', 1)).toEqual(['a', 'b', 'c']);
  });

  it('puts a shown key the saved order lacks ahead of the next shown key it knows', () => {
    expect(orderAfterMove(['h:9', 'h:2'], [['h:1', 'h:2']], 0, ['h:1', 'h:2'])).toEqual([
      'h:9',
      'h:1',
      'h:2',
    ]);
  });
});

describe('planToday pauses and routine membership', () => {
  const planOn = (habits: Habit[], on: string) => planToday(habits, [], null, on);
  const keysOn = (habits: Habit[], on: string) => planOn(habits, on).today.map((i) => i.key);
  const memberIds = (plan: ReturnType<typeof planToday>) =>
    plan.today.map((i) => [i.key, i.kind === 'routine' ? i.habits.map((h) => h.id) : []]);

  it('pauses a habit only on dates inside pausedFrom to pausedUntil', () => {
    const windowed = make(1, 'Read', { pausedFrom: '2026-11-02', pausedUntil: '2026-11-04' });
    const untilOnly = make(2, 'Tea', { pausedUntil: '2026-11-03' });
    const both = [windowed, untilOnly];

    expect(keysOn(both, '2026-11-01')).toEqual(['h:1']);
    expect(keysOn(both, '2026-11-03')).toEqual([]);
    expect(planOn(both, '2026-11-03').notToday.map((i) => i.key)).toEqual(['h:1', 'h:2']);
    expect(keysOn(both, '2026-11-04')).toEqual(['h:2']);
    expect(keysOn(both, '2026-11-05')).toEqual(['h:1', 'h:2']);
  });

  it("applies pausedToday to today's date only", () => {
    const pausedToday = make(1, 'Read', { pausedToday: true });

    expect(keysOn([pausedToday], date)).toEqual([]);
    expect(keysOn([pausedToday], '2026-11-02')).toEqual(['h:1']);
  });

  it('places a habit that is in two routines once, in the first routine', () => {
    const plan = planToday(
      [make(1, 'Read'), make(2, 'Tea')],
      [routine(5, [1]), routine(6, [1, 2])],
      null,
      date,
    );

    expect(memberIds(plan)).toEqual([
      ['r:5', [1]],
      ['r:6', [2]],
    ]);
  });

  it('ignores routine members that are missing or let go, and drops a routine left with none', () => {
    const plan = planToday(
      [make(1, 'Read'), letGoHabit],
      [routine(5, [404, letGoHabit.id]), routine(6, [letGoHabit.id, 1, 404])],
      null,
      date,
    );

    expect(memberIds(plan)).toEqual([['r:6', [1]]]);
    expect(plan.letGo).toEqual([letGoHabit]);
  });

  it('lists a paused routine member under Not for today and keeps the routine for the others', () => {
    const plan = planToday(
      [make(1, 'Read', { pausedToday: true }), make(2, 'Tea')],
      [routine(5, [1, 2])],
      null,
      date,
    );

    expect(memberIds(plan)).toEqual([['r:5', [2]]]);
    expect(plan.notToday.map((i) => i.key)).toEqual(['h:1']);
  });

  it('leaves paused and week-done habits out of the widget day', () => {
    const paused = make(1, 'Read', { pausedToday: true });

    expect(
      widgetDay([paused, weekly(2, 2, 2, 0), make(3, 'Tea')], [], null, date).map((h) => h.id),
    ).toEqual([3]);
  });
});

describe('Today order in the other sections', () => {
  const planWith = (habits: AnyHabit[], order: string[] | null) =>
    planToday(habits, [], order, date);

  it('orders the let-go section by reminder time, then name, and by the saved order once there is one', () => {
    const coffee = { ...letGoHabit, id: 11, name: 'Coffee' };
    const soda = { ...letGoHabit, id: 12, name: 'Soda' };
    const sweets = {
      ...letGoHabit,
      id: 13,
      name: 'Sweets',
      reminderTime: '09:00',
      reminderTimes: ['09:00'],
    };

    expect(planWith([soda, sweets, coffee], null).letGo.map((h) => h.id)).toEqual([13, 11, 12]);
    expect(planWith([soda, sweets, coffee], ['h:12', 'h:11']).letGo.map((h) => h.id)).toEqual([
      12, 11, 13,
    ]);
  });

  it('orders Not for today by the saved order', () => {
    const a = make(1, 'Aaa', { pausedToday: true });
    const b = make(2, 'Bbb', { pausedToday: true });

    expect(planWith([a, b], null).notToday.map((i) => i.key)).toEqual(['h:1', 'h:2']);
    expect(planWith([a, b], ['h:2', 'h:1']).notToday.map((i) => i.key)).toEqual(['h:2', 'h:1']);
  });

  it('breaks a tie on time and name by id, ignoring case', () => {
    const at8 = { reminderTime: '08:00', reminderTimes: ['08:00'] };

    expect(topKeys([make(3, 'read', at8), make(2, 'Read', at8)])).toEqual(['h:2', 'h:3']);
  });
});
