import { afterEach, describe, expect, it } from 'vitest';
import { readReward, themeSeconds } from './reward';

const setVar = (name: string, value: string) =>
  document.documentElement.style.setProperty(name, value);

afterEach(() => document.documentElement.removeAttribute('style'));

describe('readReward', () => {
  it.each(['glow', 'fill', 'rise'])('reads the theme keyword %s', (keyword) => {
    setVar('--ember-reward', ` ${keyword} `);

    expect(readReward(false)).toBe(keyword);
  });

  it.each(['none', 'sparkle', ''])('gives no reward for the value "%s"', (value) => {
    setVar('--ember-reward', value);

    expect(readReward(false)).toBeNull();
  });

  it('gives no reward when the user prefers reduced motion', () => {
    setVar('--ember-reward', 'glow');

    expect(readReward(true)).toBeNull();
  });
});

describe('themeSeconds', () => {
  it.each([
    ['180ms', 0.18],
    ['0.25s', 0.25],
    ['2', 2],
  ])('reads %s as %d seconds', (raw, seconds) => {
    setVar('--ember-dur', raw);

    expect(themeSeconds('--ember-dur', 9)).toBeCloseTo(seconds);
  });

  it('falls back when the token is missing or not a number', () => {
    setVar('--ember-dur-slow', 'slow');

    expect(themeSeconds('--ember-dur', 0.3)).toBe(0.3);
    expect(themeSeconds('--ember-dur-slow', 0.6)).toBe(0.6);
  });
});
