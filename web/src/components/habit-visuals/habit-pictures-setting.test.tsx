import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { HabitPicturesSetting } from './habit-pictures-setting';
import { UnitsSetting } from './units-setting';

describe('HabitPicturesSetting', () => {
  it('shows the current value with what it does, and reports a flip', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<HabitPicturesSetting value={true} onChange={onChange} />);
    const toggle = screen.getByRole('switch', { name: 'Suggest pictures for habits' });
    expect(toggle).toBeChecked();
    expect(toggle).toHaveAccessibleDescription(
      /once per habit\. Pictures you already chose stay\./,
    );

    await user.click(toggle);

    expect(onChange).toHaveBeenCalledExactlyOnceWith(false);
  });
});

describe('UnitsSetting', () => {
  it('offers metric and imperial under "Units", and reports the choice', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<UnitsSetting value="metric" onChange={onChange} />);
    const select = screen.getByRole('combobox', { name: 'Units' });
    expect(select).toHaveValue('metric');
    expect(select).toHaveAccessibleDescription('For water and distances.');
    expect(screen.getByRole('option', { name: 'Imperial (fl oz, mi)' })).toBeInTheDocument();

    await user.selectOptions(select, 'Imperial (fl oz, mi)');

    expect(onChange).toHaveBeenCalledExactlyOnceWith('imperial');
  });
});
