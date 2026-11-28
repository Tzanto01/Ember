import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { HabitPicturesSetting } from './habit-pictures-setting';
import { UnitsSetting } from './units-setting';

describe('HabitPicturesSetting', () => {
  it.each([
    [true, false],
    [false, true],
  ])('shows %s and reports %s when toggled', async (value, next) => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<HabitPicturesSetting value={value} onChange={onChange} />);
    const toggle = screen.getByRole('switch', { name: 'Suggest pictures for habits' });
    expect(toggle).toHaveAttribute('aria-checked', String(value));

    await user.click(toggle);

    expect(onChange).toHaveBeenCalledExactlyOnceWith(next);
  });
});

describe('UnitsSetting', () => {
  it('shows the current system and reports imperial when chosen', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<UnitsSetting value="metric" onChange={onChange} />);
    const select = screen.getByRole('combobox', { name: 'Units' });
    expect(select).toHaveValue('metric');

    await user.selectOptions(select, 'imperial');

    expect(onChange).toHaveBeenCalledExactlyOnceWith('imperial');
  });
});
