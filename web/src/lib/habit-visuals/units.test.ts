import { describe, expect, it } from 'vitest';
import { formatAmount, goalTaps, progressTotal, toCanonical, toTyped } from './units';

const text = (
  amount: number,
  unit: 'ml' | 'm' | 'min' | 'pages',
  units: 'metric' | 'imperial',
  locale = 'en',
) => formatAmount(amount, unit, units, locale).text;

describe('formatAmount and progressTotal', () => {
  it('converts the total once from count × portion, not per tap', () => {
    const bottle = { portionAmount: 250, portionUnit: 'ml' as const };

    expect(progressTotal(4, bottle, 'metric', 'en')?.text).toBe('1 L');
    expect(progressTotal(4, bottle, 'imperial', 'en')?.text).toBe('34 fl oz');
  });

  it('shows ml below 1000 and L with up to two decimals above, in the locale', () => {
    expect(text(750, 'ml', 'metric')).toBe('750 ml');
    expect(text(1250, 'ml', 'metric')).toBe('1.25 L');
    expect(text(1250, 'ml', 'metric', 'de')).toBe('1,25 L');
  });

  it('shows m below 1000 rounded to 10 m and km with one decimal above', () => {
    expect(text(150, 'm', 'metric')).toBe('150 m');
    expect(text(154, 'm', 'metric')).toBe('150 m');
    expect(text(1500, 'm', 'metric')).toBe('1.5 km');
  });

  it('shows miles with two decimals below one mile and one decimal above', () => {
    expect(text(800, 'm', 'imperial')).toBe('0.50 mi');
    expect(text(2414, 'm', 'imperial')).toBe('1.5 mi');
  });

  it('shows minutes and pages the same in both systems and leaves the page word to translation', () => {
    expect(text(30, 'min', 'imperial')).toBe('30 min');
    expect(formatAmount(40, 'pages', 'imperial', 'en')).toEqual({
      value: 40,
      number: '40',
      unit: 'pages',
      text: null,
    });
  });

  it('has no total for a habit without a portion', () => {
    expect(progressTotal(3, { portionAmount: null, portionUnit: null }, 'metric', 'en')).toBeNull();
  });
});

describe('typed portions', () => {
  it('stores fl oz and miles as whole ml and meters', () => {
    expect(toCanonical(8, 'ml', 'imperial')).toBe(237);
    expect(toCanonical(1, 'm', 'imperial')).toBe(1609);
    expect(toCanonical(250, 'ml', 'metric')).toBe(250);
  });

  it('prefills a stored portion in the typed unit', () => {
    expect(toTyped(250, 'ml', 'imperial')).toBe(8.5);
    expect(toTyped(1609, 'm', 'imperial')).toBe(1);
  });
});

describe('goalTaps', () => {
  it('rounds the taps up and reports the real total', () => {
    expect(goalTaps(2000, 300)).toEqual({
      timesPerDay: 7,
      capped: false,
      total: 2100,
      minPortion: 200,
    });
  });

  it('keeps 10 taps above the limit and names the smallest portion that fits', () => {
    expect(goalTaps(3000, 250)).toEqual({
      timesPerDay: 10,
      capped: true,
      total: 2500,
      minPortion: 300,
    });
  });
});
