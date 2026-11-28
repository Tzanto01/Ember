import { describe, expect, it } from 'vitest';
import fixture from '../../../../docs/contracts/fixtures/pictures-v1.json';
import type { HabitKind } from '../models';
import { defaultPortions, pictureError } from './picture-rules';

describe('client picture rules match the shared pictures fixture', () => {
  it('gives each picture the shared default portion', () => {
    expect(defaultPortions).toEqual(fixture.defaults);
  });

  it.each(fixture.cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
    const error = pictureError(c.kind as HabitKind, c.picture, c.portionAmount, c.portionUnit);

    expect(error).toEqual(c.valid ? null : { field: c.field, message: c.message });
  });
});
