import { useTranslation } from 'react-i18next';
import { DayCell } from '@/components/day-cell';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { useStore } from '@/lib/store';
import { chooseTheme, currentTheme, type ThemeId, themeApplied, themes } from '@/lib/theme';

function ThemePreview({ id }: { id: ThemeId }) {
  return (
    <span
      data-theme={id}
      aria-hidden="true"
      className="flex shrink-0 flex-col gap-1.5 rounded-lg bg-background p-2 font-sans text-foreground"
    >
      <span className="flex items-center justify-between gap-2 rounded-lg border-[length:var(--border-width)] border-border bg-card px-2 py-1.5 shadow-card">
        <span className="font-display text-[1rem] leading-none font-[number:var(--ember-heading-weight)] tracking-[var(--ember-heading-tracking)]">
          Aa
        </span>
        <span className="h-[12px] w-[24px] rounded-sm bg-primary" />
      </span>
      <span className="flex [--day-gap:4px] gap-[4px]">
        <DayCell state="done" size="swatch" runToNext />
        <DayCell state="lowday" size="swatch" runToNext />
        <DayCell state="done" size="swatch" />
        <DayCell state="pending" size="swatch" />
      </span>
    </span>
  );
}

/**
 * Settings → Appearance: the four themes as a radio group, each with a live preview in its own tokens; applies
 * instantly, then calls `onChoose` (for example to save it to the account).
 */
export function ThemePicker({
  labelledBy,
  describedBy,
  onChoose,
}: {
  labelledBy: string;
  describedBy?: string;
  onChoose?: (id: ThemeId) => void;
}) {
  const { t: translate } = useTranslation();
  useStore(themeApplied);
  const theme = currentTheme();

  function choose(value: string): void {
    const id = themes.find((t) => t.id === value)?.id;
    if (!id) return;
    const card = document.querySelector(`label[for="theme-${id}"]`);
    const before = card?.getBoundingClientRect().top;
    const keepInPlace = () => {
      const delta = card && before !== undefined ? card.getBoundingClientRect().top - before : 0;
      if (delta) window.scrollBy({ top: delta, behavior: 'instant' });
    };
    chooseTheme(id);
    keepInPlace();
    void document.fonts?.ready.then(keepInPlace);
    onChoose?.(id);
  }

  return (
    <RadioGroup
      aria-labelledby={labelledBy}
      aria-describedby={describedBy}
      value={theme}
      onValueChange={choose}
      className="grid gap-[12px] sm:grid-cols-2"
    >
      {themes.map((t) => (
        <label
          key={t.id}
          htmlFor={`theme-${t.id}`}
          className="flex min-h-[44px] cursor-pointer items-start gap-[12px] rounded-lg border-[length:max(1px,var(--border-width))] border-border p-[12px] has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-accent has-[[data-state=checked]]:ring-1 has-[[data-state=checked]]:ring-primary has-[[data-state=checked]]:text-accent-foreground"
        >
          <RadioGroupItem
            id={`theme-${t.id}`}
            value={t.id}
            aria-labelledby={`theme-${t.id}-name`}
            aria-describedby={`theme-${t.id}-description`}
          />
          <span className="flex min-w-0 flex-col items-start gap-[8px]">
            <span className="flex flex-col">
              <span id={`theme-${t.id}-name`} className="font-medium">
                {t.name}
              </span>
              <span id={`theme-${t.id}-description`} className="text-muted-foreground">
                {translate(t.description)}
              </span>
            </span>
            <ThemePreview id={t.id} />
          </span>
        </label>
      ))}
    </RadioGroup>
  );
}
