import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import { SettingsLinkRow, SettingsRow } from '@/components/settings/settings-row';
import { Switch } from '@/components/ui/switch';

function Row({ device, hint }: { device?: boolean; hint?: string }) {
  const [on, setOn] = useState(false);
  return (
    <SettingsRow
      id="sound"
      controlId="sound-switch"
      label="Sound"
      hint={hint}
      device={device}
      control={(field) => <Switch {...field} checked={on} onCheckedChange={setOn} />}
      nested={<p>Only while on</p>}
    >
      <p role="status">Saved.</p>
    </SettingsRow>
  );
}

describe('SettingsRow', () => {
  it('names the control by its label and describes it by the hint and the "This device" chip', async () => {
    const user = userEvent.setup();
    render(<Row hint="A soft chime" device />);

    const control = screen.getByRole('switch', { name: 'Sound' });
    expect(control).toHaveAccessibleDescription('A soft chime This device');
    await user.click(screen.getByText('Sound'));
    expect(control).toBeChecked();
  });

  it('describes nothing without a hint or chip', () => {
    render(<Row />);

    expect(screen.getByRole('switch', { name: 'Sound' })).not.toHaveAttribute('aria-describedby');
    expect(screen.queryByText('This device')).toBeNull();
  });

  it('keeps its address and a focus target for it, with its status and nested settings inside', () => {
    render(<Row hint="A soft chime" />);

    const row = document.getElementById('sound');
    const target = document.getElementById('sound-heading');
    target?.focus();
    expect(target).toHaveFocus();
    expect(target).toHaveTextContent('Sound');
    expect(row).toContainElement(screen.getByRole('status'));
    expect(row).toContainElement(screen.getByText('Only while on'));
  });
});

describe('SettingsLinkRow', () => {
  it('links by its label, describes the link by the hint and focuses the link for its address', () => {
    render(
      <MemoryRouter>
        <SettingsLinkRow
          id="routines"
          to="/routines"
          label="Routines"
          hint="Group habits that go together"
        />
      </MemoryRouter>,
    );

    const link = screen.getByRole('link', { name: 'Routines' });
    expect(link).toHaveAttribute('href', '/routines');
    expect(link).toHaveAccessibleDescription('Group habits that go together');
    expect(document.getElementById('routines')).toContainElement(link);
    expect(document.getElementById('routines-heading')).toBe(link);
  });
});
