import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { CheckButton } from './check-button';

function show(count: number, target: number) {
  render(
    <CheckButton habitName="Water" pressed={false} busy={false} reward={null} onToggle={() => {}} onRewardEnd={() => {}} count={count} target={target} />,
  );
  return screen.getByRole('button');
}

describe('CheckButton', () => {
  it.each([
    [0, 3, '0/3'],
    [1, 3, '1/3'],
    [5, 3, '3/3'],
  ])('names a multi-count button with the count its face shows (%i of %i)', (count, target, face) => {
    const button = show(count, target);
    expect(button).toHaveTextContent(face);
    expect(button).toHaveAccessibleName(`Check in Water for today, ${face} done`);
  });
});
