import type { UseQueryResult } from '@tanstack/react-query';
import { type ReactNode, useEffect } from 'react';
import { errorText } from '@/api/client';
import { formatAgo } from '@/lib/format';
import { useNow } from '@/lib/use-now';

export function Panel({ title, children, className = '' }: { title: string; children: ReactNode; className?: string }) {
  const id = `panel-${title.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`;
  return (
    <section aria-labelledby={id} className={`rounded-lg bg-card p-4 text-card-foreground shadow-card sm:p-5 ${className}`}>
      <h2 id={id} className="mb-3 text-lg">
        {title}
      </h2>
      {children}
    </section>
  );
}

export function Tile({ label, value, hint }: { label: string; value: ReactNode; hint?: ReactNode }) {
  return (
    <div className="rounded-lg bg-card p-4 shadow-card">
      <dt className="text-sm text-muted-foreground">{label}</dt>
      <dd className="m-0 font-display text-3xl font-extrabold tabular-nums">{value}</dd>
      {hint && <dd className="m-0 text-sm text-muted-foreground">{hint}</dd>}
    </div>
  );
}

export function Stats({ items }: { items: [string, ReactNode][] }) {
  return (
    <dl className="m-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
      {items.map(([k, v]) => (
        <div key={k} className="contents">
          <dt className="text-muted-foreground">{k}</dt>
          <dd className="m-0 text-right font-semibold tabular-nums">{v}</dd>
        </div>
      ))}
    </dl>
  );
}

export function Note({ children }: { children: ReactNode }) {
  return <p className="m-0 rounded-md bg-muted px-3 py-2 text-muted-foreground">{children}</p>;
}

export function Badge({ children, tone = 'muted' }: { children: ReactNode; tone?: 'muted' | 'ok' | 'bad' | 'warn' }) {
  const tones = {
    muted: 'bg-muted text-muted-foreground',
    ok: 'bg-success text-success-foreground',
    bad: 'bg-destructive text-destructive-foreground',
    warn: 'bg-notice text-notice-foreground ring-1 ring-notice-border',
  };
  return <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-bold ${tones[tone]}`}>{children}</span>;
}

export function Updated({ at }: { at: string | number }) {
  const now = useNow();
  return (
    <span className="text-sm text-muted-foreground tabular-nums">
      updated {formatAgo(at, now)}
    </span>
  );
}

export function PageHeader({ title, at, fetching, stale }: { title: string; at?: string | number; fetching?: boolean; stale?: boolean }) {
  useEffect(() => {
    document.title = `${title} · Ember ops`;
  }, [title]);
  return (
    <div className="mb-5 flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
      <h1 className="m-0 text-2xl sm:text-3xl">{title}</h1>
      <p className="m-0 flex items-center gap-2">
        {stale && <Badge tone="warn">Refresh failed, showing older data</Badge>}
        {fetching && <span className="sr-only">Refreshing</span>}
        {at !== undefined && <Updated at={at} />}
      </p>
    </div>
  );
}

/** Loading and error states for a page query; renders `children` with the data once it is there. */
export function Loaded<T>({ query, title, children }: { query: UseQueryResult<T>; title: string; children: (data: T) => ReactNode }) {
  if (query.data !== undefined) return <>{children(query.data)}</>;
  return (
    <>
      <PageHeader title={title} />
      {query.isError ? (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded-lg bg-card p-4 shadow-card">
          <span>Could not load: {errorText(query.error)}</span>
          <button type="button" className="btn" onClick={() => void query.refetch()}>
            Try again
          </button>
        </div>
      ) : (
        <p role="status" className="text-muted-foreground">
          Loading…
        </p>
      )}
    </>
  );
}
