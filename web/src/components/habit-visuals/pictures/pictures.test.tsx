import { act, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Habit } from '@/lib/models';
import { auth } from '@/lib/services';
import { calmMode } from '@/lib/theme';
import { habit, me } from '@/test/test-support';
import { HabitPictureControl } from './habit-picture';

const water: Habit = {
  ...habit,
  name: 'Drink water',
  timesPerDay: 8,
  picture: 'bottle',
  portionAmount: 250,
  portionUnit: 'ml',
  today: { ...habit.today, state: 'partial', count: 5 },
};

function show(subject: Habit, busy = false) {
  const onSetCount = vi.fn<(count: number) => void>();
  const view = render(<HabitPictureControl habit={subject} busy={busy} onSetCount={onSetCount} />);
  return { onSetCount, view };
}

afterEach(() => {
  vi.useRealTimers();
  auth.clearSession();
  calmMode.set(false);
});

describe('Bottle', () => {
  it('names the habit, its progress and what a tap adds, and shows the total', () => {
    show(water);

    expect(
      screen.getByRole('button', { name: 'Drink water, 5 of 8 glasses. Add 250 ml.' }),
    ).toBeInTheDocument();
    expect(screen.getByText('5 of 8, 1.25 L')).toHaveAttribute('aria-live', 'polite');
  });

  it('shows the total in fluid ounces for an imperial account, converted once from the whole amount', () => {
    auth.setMe({ ...me, units: 'imperial' });
    show({ ...water, today: { ...water.today, count: 4 } });

    expect(screen.getByText('4 of 8, 34 fl oz')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Drink water, 4 of 8 glasses. Add 8 fl oz.' }),
    ).toBeInTheDocument();
  });

  it('adds one portion per tap and sends nothing once the bottle is full', async () => {
    const { onSetCount, view } = show(water);
    await userEvent.click(screen.getByRole('button', { name: /^Drink water, 5 of 8/ }));
    expect(onSetCount).toHaveBeenCalledWith(6);

    onSetCount.mockClear();
    view.rerender(
      <HabitPictureControl
        habit={{ ...water, today: { ...water.today, state: 'done', count: 8 } }}
        busy={false}
        onSetCount={onSetCount}
      />,
    );
    await userEvent.click(
      screen.getByRole('button', { name: 'Drink water, 8 of 8 glasses. All done for today.' }),
    );
    expect(onSetCount).not.toHaveBeenCalled();
  });

  it('offers only the amounts that still fit after a press and hold, and one choice sends one count', () => {
    vi.useFakeTimers();
    const { onSetCount } = show({ ...water, today: { ...water.today, count: 6 } });
    const bottle = screen.getByRole('button', { name: /^Drink water, 6 of 8/ });

    fireEvent.pointerDown(bottle, { button: 0, clientX: 10, clientY: 10 });
    act(() => {
      vi.advanceTimersByTime(500);
    });
    fireEvent.pointerUp(bottle);
    fireEvent.click(bottle);

    const choices = screen.getByRole('group', { name: 'Amounts for Drink water' });
    expect(Array.from(choices.querySelectorAll('button'), (b) => b.textContent)).toEqual([
      '+250 ml',
      '+500 ml',
      'Close',
    ]);
    expect(onSetCount).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: '+500 ml' }));
    expect(onSetCount).toHaveBeenCalledExactlyOnceWith(8);
    expect(screen.queryByRole('group')).toBeNull();
  });

  it('a short press is a plain tap and opens nothing', () => {
    vi.useFakeTimers();
    const { onSetCount } = show(water);
    const bottle = screen.getByRole('button', { name: /^Drink water, 5 of 8/ });

    fireEvent.pointerDown(bottle, { button: 0, clientX: 10, clientY: 10 });
    act(() => {
      vi.advanceTimersByTime(200);
    });
    fireEvent.pointerUp(bottle);
    fireEvent.click(bottle);
    act(() => {
      vi.advanceTimersByTime(500);
    });

    expect(onSetCount).toHaveBeenCalledExactlyOnceWith(6);
    expect(screen.queryByRole('group')).toBeNull();
  });

  it('opens the amounts from the keyboard with "More", and Escape closes them and returns focus', async () => {
    const { onSetCount } = show(water);
    const more = screen.getByRole('button', { name: 'More for Drink water' });
    more.focus();
    await userEvent.keyboard('{Enter}');

    expect(more).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('button', { name: '+250 ml' })).toHaveFocus();
    expect(screen.getByRole('button', { name: '+750 ml' })).toBeInTheDocument();
    await userEvent.keyboard('{Escape}');

    expect(screen.queryByRole('group')).toBeNull();
    expect(more).toHaveFocus();
    expect(onSetCount).not.toHaveBeenCalled();
  });
});

describe('Pills', () => {
  const tablets: Habit = {
    ...habit,
    name: 'Take tablets',
    timesPerDay: 3,
    picture: 'pills',
    today: { ...habit.today, state: 'partial', count: 2 },
  };

  it('labels the slots with the reminder times when there is one per slot, and shows the count only', () => {
    show({ ...tablets, reminderTimes: ['08:00', '13:00', '20:00'] });

    expect(screen.getByRole('button', { name: 'Take tablets, 8:00 AM' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByRole('button', { name: 'Take tablets, 8:00 PM' })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
    expect(screen.getByText('2 of 3')).toHaveAttribute('aria-live', 'polite');
  });

  it('labels the slots 1st, 2nd, 3rd otherwise, and 1., 2., 3. in German', async () => {
    show({ ...tablets, reminderTimes: ['08:00'] });
    expect(screen.getAllByRole('button').map((b) => b.getAttribute('aria-label'))).toEqual([
      'Take tablets, 1st',
      'Take tablets, 2nd',
      'Take tablets, 3rd',
    ]);

    const { default: i18n } = await import('@/lib/i18n');
    await act(() => i18n.changeLanguage('de'));
    expect(screen.getByRole('button', { name: 'Take tablets, 3.' })).toBeInTheDocument();
    expect(screen.getByText('2 von 3')).toBeInTheDocument();
  });

  it('checks in once from an open slot and undoes back to a taken one', async () => {
    const { onSetCount } = show(tablets);

    await userEvent.click(screen.getByRole('button', { name: 'Take tablets, 3rd' }));
    expect(onSetCount).toHaveBeenLastCalledWith(3);
    await userEvent.click(screen.getByRole('button', { name: 'Take tablets, 1st' }));
    expect(onSetCount).toHaveBeenLastCalledWith(0);
  });
});

describe('Kindling', () => {
  it('adds one log per tap and says what a tap does', async () => {
    const { onSetCount } = show({
      ...habit,
      name: 'Stretch',
      timesPerDay: 3,
      picture: 'kindling',
      today: { ...habit.today, count: 1 },
    });

    await userEvent.click(
      screen.getByRole('button', { name: 'Stretch, 1 of 3 logs. Add a log to the fire.' }),
    );
    expect(onSetCount).toHaveBeenCalledWith(2);
    expect(screen.getByText('1 of 3')).toBeInTheDocument();
  });

  it('ignores taps while the last one is still saving', async () => {
    const { onSetCount } = show(
      { ...habit, name: 'Stretch', timesPerDay: 3, picture: 'kindling' },
      true,
    );

    await userEvent.click(screen.getByRole('button', { name: /^Stretch/ }));
    expect(onSetCount).not.toHaveBeenCalled();
  });
});

describe('Shelf and trail', () => {
  const reading: Habit = {
    ...habit,
    name: 'Read',
    timesPerDay: 3,
    picture: 'shelf',
    portionAmount: 15,
    portionUnit: 'min',
    today: { ...habit.today, state: 'partial', count: 2 },
  };

  it('shows reading time or pages read, and adds one session per tap', async () => {
    const { onSetCount, view } = show(reading);
    expect(screen.getByText('2 of 3, 30 min')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Read, 2 of 3 books. Add 15 min.' }));
    expect(onSetCount).toHaveBeenCalledWith(3);

    view.rerender(
      <HabitPictureControl
        habit={{ ...reading, portionAmount: 20, portionUnit: 'pages' }}
        busy={false}
        onSetCount={onSetCount}
      />,
    );
    expect(screen.getByText('2 of 3, 40 pages')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Read, 2 of 3 books. Add 20 pages.' }),
    ).toBeInTheDocument();
  });

  it('shows a walked distance in km, or in miles for an imperial account', () => {
    const walk: Habit = {
      ...reading,
      name: 'Walk',
      timesPerDay: 2,
      picture: 'trail',
      portionAmount: 1500,
      portionUnit: 'm',
      today: { ...habit.today, state: 'partial', count: 1 },
    };
    const { view } = show(walk);
    expect(screen.getByText('1 of 2, 1.5 km')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Walk, 1 of 2 stops. Add 1.5 km.' }),
    ).toBeInTheDocument();
    view.unmount();

    auth.setMe({ ...me, units: 'imperial' });
    show(walk);
    expect(screen.getByText('1 of 2, 0.93 mi')).toBeInTheDocument();
  });
});

describe('HabitPictureControl', () => {
  it('turns the fill transition off in calm mode and on otherwise', () => {
    const { view } = show(water);
    expect(view.container.querySelector('[data-picture-motion]')).not.toBeNull();

    act(() => calmMode.set(true));
    expect(view.container.querySelector('[data-picture-motion]')).toBeNull();
  });

  it('turns the fill transition off when the device asks for reduced motion', () => {
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: query === '(prefers-reduced-motion: reduce)',
      addEventListener() {},
      removeEventListener() {},
    }));
    try {
      const { view } = show(water);
      expect(view.container.querySelector('[data-picture]')).not.toBeNull();
      expect(view.container.querySelector('[data-picture-motion]')).toBeNull();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('shows no picture for a paused habit or one that has not started', () => {
    const paused = show({
      ...water,
      pausedToday: true,
      today: { ...water.today, state: 'paused' },
    });
    expect(paused.view.container).toBeEmptyDOMElement();
    paused.view.unmount();

    const later = show({ ...water, today: { ...water.today, state: 'beforestart' } });
    expect(later.view.container).toBeEmptyDOMElement();
  });
});
