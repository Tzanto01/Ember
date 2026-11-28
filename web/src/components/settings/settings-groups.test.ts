import { describe, expect, it } from 'vitest';
import { settingsBlocks, settingsGroupOf, settingsGroups } from './settings-groups';

describe('settingsGroups', () => {
  it('holds every card in its group and block, in order, with Help right now apart at the end', () => {
    expect(settingsBlocks.map((block) => block.id)).toEqual(['you', 'ember', 'support']);
    expect(settingsGroups.map(({ slug, block, cards }) => ({ slug, block, cards }))).toEqual([
      { slug: 'account', block: 'you', cards: ['device', 'account', 'two-factor', 'discord'] },
      { slug: 'language', block: 'you', cards: ['language', 'timezone'] },
      { slug: 'privacy', block: 'you', cards: ['consent', 'export', 'delete'] },
      {
        slug: 'reminders',
        block: 'ember',
        cards: ['push', 'phone-reminders', 'second-reminder', 'timer'],
      },
      { slug: 'habits', block: 'ember', cards: ['streaks', 'grace', 'habit-notes', 'routines'] },
      { slug: 'journal', block: 'ember', cards: ['journal', 'moods', 'helps', 'moments'] },
      { slug: 'appearance', block: 'ember', cards: ['appearance'] },
      { slug: 'people', block: 'ember', cards: ['fireside'] },
      {
        slug: 'support',
        block: 'support',
        cards: ['feedback', 'diagnostics', 'app-updates', 'whats-new', 'about'],
      },
      { slug: 'help', block: null, cards: ['help'] },
    ]);
  });

  it('opens the group that took the cards of an older card id', () => {
    expect(settingsGroupOf('dm-reminders')?.slug).toBe('reminders');
    expect(settingsGroupOf('nothing')).toBeUndefined();
  });
});
