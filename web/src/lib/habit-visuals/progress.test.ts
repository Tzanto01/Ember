import { describe, expect, it } from 'vitest';
import { clampCount, holdChoices, nextCount, pillSlots, slotTapCount } from './progress';

describe('picture taps', () => {
  it('adds one check-in per tap and does nothing once full', () => {
    expect(nextCount(4, 8)).toBe(5);
    expect(nextCount(8, 8)).toBeNull();
  });

  it('clamps a stale count to the times per day before it is sent', () => {
    expect(clampCount(5, 3)).toBe(3);
    expect(clampCount(-1, 3)).toBe(0);
    expect(nextCount(9, 3)).toBeNull();
  });

  it('offers only the hold choices that fit what is left', () => {
    expect(holdChoices(6, 8)).toEqual([1, 2]);
    expect(holdChoices(8, 8)).toEqual([]);
  });
});

describe('pill box', () => {
  it('labels slots with the reminder times when there is one per slot, else with ordinals', () => {
    expect(pillSlots({ timesPerDay: 2, reminderTimes: ['08:00', '20:00'] }, 1)).toEqual([
      { index: 0, taken: true, label: { kind: 'time', time: '08:00' } },
      { index: 1, taken: false, label: { kind: 'time', time: '20:00' } },
    ]);
    expect(
      pillSlots({ timesPerDay: 3, reminderTimes: ['08:00'] }, 0).map((slot) => slot.label),
    ).toEqual([
      { kind: 'ordinal', position: 1 },
      { kind: 'ordinal', position: 2 },
      { kind: 'ordinal', position: 3 },
    ]);
  });

  it('fills the next open slot on an open slot and undoes back to a taken slot', () => {
    expect(slotTapCount(2, 1, 3)).toBe(2);
    expect(slotTapCount(0, 2, 3)).toBe(0);
    expect(slotTapCount(1, 2, 3)).toBe(1);
  });
});
