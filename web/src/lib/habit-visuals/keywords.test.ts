import { describe, expect, it } from 'vitest';
import { matchPicture } from './keywords';

describe('matchPicture', () => {
  it('matches a short keyword as a whole word or with a common ending', () => {
    expect(matchPicture('Morning running')).toBe('trail');
    expect(matchPicture('Go hiking')).toBe('trail');
    expect(matchPicture('Two walks')).toBe('trail');
    expect(matchPicture('10,000 steps')).toBe('trail');
    expect(matchPicture('Reading time')).toBe('shelf');
    expect(matchPicture('Library books')).toBe('shelf');
    expect(matchPicture('Evening pills')).toBe('pills');
    expect(matchPicture('Morning meds')).toBe('pills');
  });

  it('does not match a short keyword inside another word', () => {
    expect(matchPicture('Sunday brunch')).toBeNull();
    expect(matchPicture('Get ready for bed')).toBeNull();
    expect(matchPicture('Pillow spray')).toBeNull();
  });

  it('matches a longer keyword inside a German compound', () => {
    expect(matchPicture('Spazierengehen')).toBe('trail');
    expect(matchPicture('Wassertrinken')).toBe('bottle');
    expect(matchPicture('Mineralwasser')).toBe('bottle');
    expect(matchPicture('Morgenmedikamente')).toBe('pills');
  });

  it('splits on anything that is not a letter and ignores case', () => {
    expect(matchPicture('10min-BUCH/lesen')).toBe('shelf');
  });

  it('prefers the earlier picture when a name matches several', () => {
    expect(matchPicture('Walk and drink water')).toBe('bottle');
  });

  it('never suggests kindling', () => {
    expect(matchPicture('Light the kindling for a fire')).toBeNull();
  });
});
