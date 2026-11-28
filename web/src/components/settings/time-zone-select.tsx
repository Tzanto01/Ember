import { Check, ChevronDown } from 'lucide-react';
import { type KeyboardEvent, useEffect, useId, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { controlClass } from '@/components/ui/input';
import { env } from '@/lib/env';
import { timeZoneLabel } from '@/lib/local-date';
import { cn } from '@/lib/utils';

const fallbackZones = [
  'UTC',
  'Europe/London',
  'Europe/Lisbon',
  'Europe/Berlin',
  'Europe/Vienna',
  'Europe/Zurich',
  'Europe/Paris',
  'Europe/Madrid',
  'Europe/Rome',
  'Europe/Amsterdam',
  'Europe/Warsaw',
  'Europe/Athens',
  'Europe/Istanbul',
  'Africa/Cairo',
  'Africa/Johannesburg',
  'Asia/Dubai',
  'Asia/Kolkata',
  'Asia/Bangkok',
  'Asia/Shanghai',
  'Asia/Tokyo',
  'Australia/Sydney',
  'Pacific/Auckland',
  'America/Sao_Paulo',
  'America/New_York',
  'America/Chicago',
  'America/Denver',
  'America/Los_Angeles',
  'America/Anchorage',
  'Pacific/Honolulu',
];

/** City names people type that the IANA ids don't hold, and the zone each lies in. */
const cityAliases: Record<string, string> = {
  München: 'Europe/Berlin',
  Munich: 'Europe/Berlin',
  Hamburg: 'Europe/Berlin',
  Köln: 'Europe/Berlin',
  Cologne: 'Europe/Berlin',
  Frankfurt: 'Europe/Berlin',
  Stuttgart: 'Europe/Berlin',
  Düsseldorf: 'Europe/Berlin',
  Leipzig: 'Europe/Berlin',
  Dresden: 'Europe/Berlin',
  Hannover: 'Europe/Berlin',
  Wien: 'Europe/Vienna',
  Graz: 'Europe/Vienna',
  Salzburg: 'Europe/Vienna',
  Genf: 'Europe/Zurich',
  Geneva: 'Europe/Zurich',
  Bern: 'Europe/Zurich',
  Basel: 'Europe/Zurich',
  Rom: 'Europe/Rome',
  Mailand: 'Europe/Rome',
  Milan: 'Europe/Rome',
  Prag: 'Europe/Prague',
  Warschau: 'Europe/Warsaw',
  Moskau: 'Europe/Moscow',
  Lissabon: 'Europe/Lisbon',
  Brüssel: 'Europe/Brussels',
  Kopenhagen: 'Europe/Copenhagen',
};

/** Lower case, without accents or umlauts, `_` as a space: "Zürich" and "zurich" match. */
const fold = (text: string) =>
  text
    .normalize('NFD')
    .replace(/\p{M}/gu, '')
    .toLowerCase()
    .replaceAll('ß', 'ss')
    .replace(/[_\s]+/g, ' ')
    .trim();

const aliasesByZone = new Map<string, string[]>();
for (const [city, zone] of Object.entries(cityAliases))
  aliasesByZone.set(zone, [...(aliasesByZone.get(zone) ?? []), fold(city)]);

let knownZones: readonly string[] | null = null;

/** Every zone this runtime names, or the short fallback list, plus UTC (which V8 leaves out); worked out once. */
function supportedZones(): readonly string[] {
  knownZones ??= [
    ...new Set([
      ...(typeof Intl.supportedValuesOf === 'function'
        ? Intl.supportedValuesOf('timeZone')
        : fallbackZones),
      'UTC',
    ]),
  ].sort();
  return knownZones;
}

const labels = new Map<string, string>();

/** "America/New York (UTC-05:00)", worked out the first time an option shows. */
function zoneLabel(zone: string): string {
  let label = labels.get(zone);
  if (label === undefined) {
    label = zoneName(timeZoneLabel(zone));
    labels.set(zone, label);
  }
  return label;
}

/**
 * How well `zone` matches the folded `query`: 0 when its city (or a city alias) starts with it, 1 when any other part
 * of its id or an alias holds it, null when nothing does. The offset never counts.
 */
function rankOf(zone: string, query: string): number | null {
  const parts = zone.split('/').map(fold);
  const aliases = aliasesByZone.get(zone) ?? [];
  if ((parts.at(-1) ?? '').startsWith(query) || aliases.some((alias) => alias.startsWith(query)))
    return 0;
  const id = parts.join('/');
  if (
    id.includes(query) ||
    id.replaceAll('/', ' ').includes(query) ||
    aliases.some((alias) => alias.includes(query))
  )
    return 1;
  return null;
}

/** The zones that match `query`, best first (city prefix matches, then the rest, each by city); all of them for none. */
function search(zones: readonly string[], query: string | null): string[] {
  const folded = query === null ? '' : fold(query);
  if (!folded) return [...zones];
  const city = (zone: string) => fold(zone.split('/').at(-1) ?? zone);
  return zones
    .flatMap((zone) => {
      const rank = rankOf(zone, folded);
      return rank === null ? [] : [{ zone, rank }];
    })
    .sort(
      (a, b) =>
        a.rank - b.rank ||
        city(a.zone).localeCompare(city(b.zone), 'en') ||
        a.zone.localeCompare(b.zone),
    )
    .map(({ zone }) => zone);
}

/** "America/New York" for `America/New_York`. */
const zoneName = (zone: string) => zone.replaceAll('_', ' ');

export interface TimeZoneSelectProps {
  /** The text field's id, for its label. */
  id: string;
  /** The zone in use, an IANA id. */
  value: string;
  /** This device's zone, offered at the top of the list. */
  suggested?: string | null;
  'aria-describedby'?: string;
  /** Called with the IANA id the person picks. */
  onChoose: (zone: string) => void;
}

/**
 * A searchable list of every IANA time zone (a short list where the runtime can't name them all, plus `value` and
 * `suggested` always): type to filter, ArrowUp/ArrowDown move, Enter or a tap picks, Escape, Tab or leaving the field
 * closes it and puts the zone in use back. `suggested` stands first, under "Your phone says …" (in the app) or "Your
 * browser says …". The list scrolls into view above the tab bar when it opens.
 */
export function TimeZoneSelect({
  id,
  value,
  suggested,
  'aria-describedby': describedBy,
  onChoose,
}: TimeZoneSelectProps) {
  const { t } = useTranslation();
  const base = useId();
  const listId = `${base}-list`;
  const optionId = (index: number) => `${base}-option-${index}`;
  const list = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState<string | null>(null);
  const [active, setActive] = useState(-1);
  const [announced, setAnnounced] = useState('');

  const suggestion = suggested ?? null;
  const zones = useMemo(() => {
    const known = supportedZones();
    const extra = [value, suggestion].filter(
      (zone, index, all): zone is string =>
        !!zone && !known.includes(zone) && all.indexOf(zone) === index,
    );
    return extra.length > 0 ? [...known, ...extra].sort() : known;
  }, [value, suggestion]);

  function optionsFor(text: string | null): { suggested: boolean; zones: string[] } {
    const found = search(zones, text);
    const first = !!suggestion && found.includes(suggestion);
    return {
      suggested: first,
      zones:
        first && suggestion ? [suggestion, ...found.filter((zone) => zone !== suggestion)] : found,
    };
  }

  const shown = optionsFor(query);
  const options = shown.zones;
  const expanded = open && options.length > 0;
  const message =
    open && query !== null
      ? options.length > 0
        ? t('settings.timezone.results', { count: options.length })
        : t('settings.timezone.none')
      : '';

  useEffect(() => {
    const timer = setTimeout(() => setAnnounced(message), 400);
    return () => clearTimeout(timer);
  }, [message]);

  useEffect(() => {
    const box = list.current;
    const option =
      expanded && active >= 0 ? document.getElementById(`${base}-option-${active}`) : null;
    if (!box || !option) return;
    if (option.offsetTop < box.scrollTop) box.scrollTop = option.offsetTop;
    else if (option.offsetTop + option.offsetHeight > box.scrollTop + box.clientHeight)
      box.scrollTop = option.offsetTop + option.offsetHeight - box.clientHeight;
  }, [active, expanded, base]);

  useEffect(() => {
    if (expanded) list.current?.scrollIntoView?.({ block: 'nearest' });
  }, [expanded]);

  function show(at: 'current' | 'last'): void {
    const all = optionsFor(query).zones;
    const current = all.indexOf(value);
    setOpen(true);
    setActive(at === 'last' && current < 0 ? all.length - 1 : Math.max(current, 0));
  }

  function close(): void {
    setOpen(false);
    setQuery(null);
    setActive(-1);
  }

  function choose(zone: string): void {
    close();
    onChoose(zone);
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>): void {
    const last = options.length - 1;
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        if (!open) show('current');
        else if (!event.altKey) setActive(Math.min(active + 1, last));
        return;
      case 'ArrowUp':
        event.preventDefault();
        if (!open) show('last');
        else setActive(Math.max(active - 1, 0));
        return;
      case 'Enter': {
        if (event.nativeEvent.isComposing) return;
        event.preventDefault();
        const zone = expanded ? options[active] : undefined;
        if (zone) choose(zone);
        else if (!open) show('current');
        return;
      }
      case 'Escape':
        if (!open && query === null) return;
        event.preventDefault();
        close();
        return;
    }
  }

  function renderOption(zone: string, index: number) {
    return (
      <div
        key={zone}
        id={optionId(index)}
        role="option"
        aria-selected={index === active}
        data-active={index === active || undefined}
        onClick={() => choose(zone)}
        className="flex min-h-[44px] cursor-pointer items-center gap-2 rounded-sm px-3 py-2 text-base hover:bg-accent hover:text-accent-foreground data-[active]:bg-accent data-[active]:text-accent-foreground data-[active]:ring-1 data-[active]:ring-primary data-[active]:ring-inset"
      >
        <span className="min-w-0 flex-1 break-words">{zoneLabel(zone)}</span>
        {zone === value && (
          <>
            <Check aria-hidden className="size-[18px] shrink-0 text-primary" />
            <span className="sr-only">{t('settings.timezone.inUse')}</span>
          </>
        )}
      </div>
    );
  }

  const rest = shown.suggested ? options.slice(1) : options;
  const offset = shown.suggested ? 1 : 0;
  const suggestionLabel =
    suggestion &&
    t(env.native ? 'settings.timezone.phoneSays' : 'settings.timezone.browserSays', {
      zone: zoneName(suggestion),
    });

  return (
    <div className="flex flex-col">
      <div className="relative">
        <input
          id={id}
          type="text"
          role="combobox"
          aria-autocomplete="list"
          aria-expanded={expanded}
          aria-controls={expanded ? listId : undefined}
          aria-activedescendant={expanded && active >= 0 ? optionId(active) : undefined}
          aria-describedby={describedBy}
          autoComplete="off"
          autoCapitalize="off"
          spellCheck={false}
          enterKeyHint="done"
          value={query ?? zoneName(value)}
          onChange={(event) => {
            const text = event.target.value;
            setQuery(text);
            setOpen(true);
            setActive(optionsFor(text).zones.length > 0 ? 0 : -1);
          }}
          onFocus={(event) => event.target.select()}
          onClick={() => {
            if (!open) show('current');
          }}
          onKeyDown={onKeyDown}
          onBlur={close}
          className={cn(controlClass, 'pr-11')}
        />
        <ChevronDown
          aria-hidden
          className="pointer-events-none absolute top-1/2 right-3 size-[18px] -translate-y-1/2 text-muted-foreground"
        />
      </div>
      {expanded && (
        <div
          ref={list}
          id={listId}
          role="listbox"
          aria-label={t('settings.timezone.listLabel')}
          onMouseDown={(event) => event.preventDefault()}
          className="relative mt-2 max-h-[min(20rem,50dvh)] scroll-mb-[calc(64px+env(safe-area-inset-bottom)+1rem)] overflow-y-auto overscroll-contain rounded-md border-[length:max(1px,var(--border-width))] border-border bg-popover p-1 text-popover-foreground shadow-card"
        >
          {shown.suggested && suggestion ? (
            <>
              <div role="group" aria-label={suggestionLabel ?? undefined} className="pb-1">
                <p
                  aria-hidden
                  className="px-3 pt-1 pb-0.5 text-[0.875rem] font-semibold text-muted-foreground"
                >
                  {suggestionLabel}
                </p>
                {renderOption(suggestion, 0)}
              </div>
              <div
                role="group"
                aria-label={t('settings.timezone.allZones')}
                className="border-t border-border pt-1"
              >
                <p
                  aria-hidden
                  className="px-3 pt-1 pb-0.5 text-[0.875rem] font-semibold text-muted-foreground"
                >
                  {t('settings.timezone.allZones')}
                </p>
                {rest.map((zone, index) => renderOption(zone, index + offset))}
              </div>
            </>
          ) : (
            rest.map((zone, index) => renderOption(zone, index + offset))
          )}
        </div>
      )}
      {open && options.length === 0 && (
        <p className="mt-2 rounded-md bg-muted px-3 py-2 text-muted-foreground">
          {t('settings.timezone.none')}
        </p>
      )}
      <p role="status" className="sr-only">
        {announced}
      </p>
    </div>
  );
}
