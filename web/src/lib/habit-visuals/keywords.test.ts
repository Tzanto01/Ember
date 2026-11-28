import { describe, expect, it } from 'vitest';
import { matchPicture } from './keywords';

describe('matchPicture', () => {
  it('matches a short keyword only at the start of a word', () => {
    expect(matchPicture('Morning running')).toBe('trail');
    expect(matchPicture('Sunday brunch')).toBeNull();
  });

  it('matches a longer keyword inside a German compound', () => {
    expect(matchPicture('Spazierengehen')).toBe('trail');
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
