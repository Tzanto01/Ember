import { describe, expect, it } from 'vitest';
import type { FeedbackItem, FeedbackStatus } from '@/api/types';
import { applyMove, columnOf, indexAmong, positionAt } from './board';

let nextId = 1;
function card(position: number | null, status: FeedbackStatus = 'open', createdAt = '2026-11-01T10:00:00Z'): FeedbackItem {
  return {
    id: nextId++,
    title: 't',
    text: '',
    platform: 'web',
    version: null,
    createdAt,
    status,
    resolvedAt: null,
    anonymous: false,
    imageIds: [],
    commentCount: 0,
    position,
  };
}

describe('positionAt', () => {
  it('lands strictly between both neighbours', () => {
    const col = [card(1), card(2)];
    expect(positionAt(col, 1)).toBe(1.5);
  });

  it('goes before the first and after the last card', () => {
    const col = [card(4), card(8)];
    expect(positionAt(col, 0)).toBeLessThan(4);
    expect(positionAt(col, 2)).toBeGreaterThan(8);
  });

  it('sorts a card placed among unpositioned cards right after the positioned ones', () => {
    const positioned = card(5);
    const loose = [card(null), card(null)];
    const pos = positionAt([positioned, ...loose], 2);
    const moved = { ...card(pos), id: 999 };
    expect(columnOf([positioned, ...loose, moved], 'open').map((c) => c.id)).toEqual([positioned.id, moved.id, ...loose.map((c) => c.id)]);
  });
});

describe('applyMove', () => {
  it('reorders so the moved card appears at the requested index of the target column', () => {
    const a = card(1, 'planned');
    const b = card(2, 'planned');
    const c = card(3, 'planned');
    const x = card(null, 'open');
    const items = [a, b, c, x];
    for (let index = 0; index <= 3; index++) {
      const result = applyMove(items, x.id, 'planned', index)!;
      expect(columnOf(result.items, 'planned').findIndex((i) => i.id === x.id)).toBe(index);
    }
  });

  it('sets resolvedAt for released and declined, keeps an existing one, and clears it otherwise', () => {
    const x = { ...card(null), resolvedAt: '2026-10-01T00:00:00Z' };
    expect(applyMove([x], x.id, 'declined', 0)!.items[0]!.resolvedAt).toBe('2026-10-01T00:00:00Z');
    expect(applyMove([card(null)], nextId - 1, 'released', 0)!.items[0]!.resolvedAt).not.toBeNull();
    expect(applyMove([x], x.id, 'planned', 0)!.items[0]!.resolvedAt).toBeNull();
  });
});

describe('indexAmong', () => {
  it('maps a slot among filtered cards to the full column', () => {
    const [a, b, c, d] = [card(1), card(2), card(3), card(4)];
    const full = [a!, b!, c!, d!];
    const visible = [b!, d!];
    expect(indexAmong(full, visible, 0)).toBe(1);
    expect(indexAmong(full, visible, 1)).toBe(3);
    expect(indexAmong(full, visible, 2)).toBe(4);
    expect(indexAmong(full, [], 0)).toBe(0);
  });
});
