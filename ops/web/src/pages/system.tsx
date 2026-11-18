import { useQuery } from '@tanstack/react-query';
import { ExternalLink } from 'lucide-react';
import type { ReactNode } from 'react';
import { systemQuery } from '@/api/client';
import type { System } from '@/api/types';
import { Badge, Loaded, Note, PageHeader, Panel, Stats } from '@/components/ui';
import { diskTone, formatAgo, formatBytes, formatStamp, percent } from '@/lib/format';
import { useNow } from '@/lib/use-now';

const staleMs = 5 * 60 * 1000;
const fromStatusFile = new Set(['disks', 'backup', 'deployed']);
const short = (sha: string | null | undefined) => (sha ? sha.slice(0, 7) : '–');

function Unavailable({ s, section }: { s: System; section: string }) {
  const reason = s.errors[section] ?? (fromStatusFile.has(section) ? s.errors['status'] : undefined);
  return <Note>Unavailable{reason ? `: ${reason}` : ''}</Note>;
}

function Section<T>({ s, section, title, value, children }: { s: System; section: string; title: string; value: T | null; children: (v: T) => ReactNode }) {
  return <Panel title={title}>{value === null || value === undefined ? <Unavailable s={s} section={section} /> : children(value)}</Panel>;
}

interface LiveNotice {
  id?: string;
  kind?: string;
  showFrom?: string | null;
  showUntil?: string | null;
  en?: string;
  de?: string;
}

function Notices({ notice }: { notice: unknown }) {
  const list = notice && typeof notice === 'object' ? (notice as { notices?: unknown }).notices : undefined;
  if (!Array.isArray(list)) return <pre className="m-0 overflow-x-auto text-sm whitespace-pre-wrap">{JSON.stringify(notice, null, 2)}</pre>;
  if (list.length === 0) return <Note>No live notice</Note>;
  return (
    <ul className="m-0 flex list-none flex-col gap-3 p-0">
      {(list as LiveNotice[]).map((n, i) => (
        <li key={n.id ?? i} className="rounded-md bg-notice p-3 text-notice-foreground ring-1 ring-notice-border">
          <div className="mb-1 flex flex-wrap items-center gap-2 text-sm">
            <Badge>{n.kind ?? 'notice'}</Badge>
            {n.id && <code>{n.id}</code>}
            {n.showFrom && <span>from {formatStamp(n.showFrom)}</span>}
            {n.showUntil && <span>until {formatStamp(n.showUntil)}</span>}
          </div>
          {n.en && <p className="m-0">{n.en}</p>}
          {n.de && (
            <p className="m-0 mt-1" lang="de">
              {n.de}
            </p>
          )}
        </li>
      ))}
    </ul>
  );
}

export function SystemPage() {
  const query = useQuery(systemQuery);
  const now = useNow();
  return (
    <Loaded query={query} title="System">
      {(s) => {
        const statusAge = s.statusWrittenAt ? now - Date.parse(s.statusWrittenAt) : null;
        return (
          <>
            <PageHeader title="System" at={s.generatedAt} fetching={query.isFetching} stale={query.isRefetchError} />
            <section aria-labelledby="services" className="mb-6">
              <div className="mb-3 flex flex-wrap items-baseline gap-x-3 gap-y-1">
                <h2 id="services" className="m-0 text-lg">
                  Services
                </h2>
                {statusAge === null ? (
                  <Badge tone="bad">Host status unavailable{s.errors['status'] ? `: ${s.errors['status']}` : ''}</Badge>
                ) : statusAge > staleMs ? (
                  <Badge tone="warn">Host status stale, written {formatAgo(s.statusWrittenAt!, now)}</Badge>
                ) : (
                  <span className="text-sm text-muted-foreground">host status written {formatAgo(s.statusWrittenAt!, now)}</span>
                )}
              </div>
              <ul className="m-0 grid list-none gap-3 p-0 sm:grid-cols-2 lg:grid-cols-5">
                {s.services.map((svc) => (
                  <li key={svc.name} className="flex flex-col gap-1 rounded-lg bg-card p-4 shadow-card">
                    <span className="font-bold">{svc.name}</span>
                    <span>{svc.ok ? <Badge tone="ok">OK</Badge> : <Badge tone="bad">Failing</Badge>}</span>
                    {svc.detail && <span className="text-sm text-muted-foreground">{svc.detail}</span>}
                  </li>
                ))}
              </ul>
            </section>
            <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
              <Section s={s} section="database" title="Database" value={s.database}>
                {(d) => (
                  <Stats
                    items={[
                      ['Size', formatBytes(d.sizeBytes)],
                      ['Latest migration', <code className="text-sm break-all">{d.latestMigration ?? '–'}</code>],
                    ]}
                  />
                )}
              </Section>
              <Section s={s} section="disks" title="Disks" value={s.disks}>
                {(disks) => (
                  <Stats
                    items={disks.map((d): [string, ReactNode] => {
                      const text = `${formatBytes(d.freeBytes)} free of ${formatBytes(d.totalBytes)} (${percent(d.freeBytes, d.totalBytes)} free)`;
                      const tone = diskTone(d.freeBytes, d.totalBytes);
                      return [d.mount, tone ? <Badge tone={tone}>{text}</Badge> : text];
                    })}
                  />
                )}
              </Section>
              <Section s={s} section="backup" title="Newest pre-deploy dump" value={s.backup}>
                {(b) => (
                  <Stats
                    items={[
                      ['File', <code className="text-sm break-all">{b.name}</code>],
                      ['Written', formatStamp(b.at)],
                      ['Age', b.ageHours < 48 ? `${Math.round(b.ageHours)} h` : `${Math.round(b.ageHours / 24)} days`],
                      ['Size', formatBytes(b.sizeBytes)],
                    ]}
                  />
                )}
              </Section>
              <Panel title="Deployed">
                <div className="flex flex-col gap-3">
                  {s.deployed === null ? (
                    <Unavailable s={s} section="deployed" />
                  ) : (
                    <Stats
                      items={[
                        ['Production', <code title={s.deployed.production ?? undefined}>{short(s.deployed.production)}</code>],
                        ['Staging', <code title={s.deployed.staging ?? undefined}>{short(s.deployed.staging)}</code>],
                      ]}
                    />
                  )}
                  <Stats items={[['Dashboard', <code title={s.dashboardSha ?? undefined}>{short(s.dashboardSha)}</code>]]} />
                </div>
              </Panel>
              <Section s={s} section="apk" title="Android APK" value={s.apk}>
                {(a) => (
                  <Stats
                    items={[
                      ['Version', a.versionName],
                      ['Version code', String(a.versionCode)],
                    ]}
                  />
                )}
              </Section>
              <Section s={s} section="notice" title="Live notice" value={s.notice === undefined ? null : s.notice}>
                {(n) => <Notices notice={n} />}
              </Section>
              <Section s={s} section="ci" title="CI" value={s.ci}>
                {(runs) => (
                  <ul className="m-0 flex list-none flex-col gap-3 p-0">
                    {runs.map((run) => {
                      const ok = run.conclusion === 'success';
                      const done = run.status === 'completed';
                      return (
                        <li key={run.branch} className="flex flex-wrap items-center gap-x-3 gap-y-1">
                          <code className="font-bold">{run.branch}</code>
                          <Badge tone={!done ? 'warn' : ok ? 'ok' : 'bad'}>{done ? (run.conclusion ?? 'unknown') : run.status}</Badge>
                          <a href={run.url} target="_blank" rel="noreferrer noopener" className="inline-flex items-center gap-1 text-primary underline">
                            <code>{short(run.sha)}</code>
                            <ExternalLink aria-hidden="true" className="size-3.5" />
                            <span className="sr-only">(GitHub run for {run.branch}, opens in a new tab)</span>
                          </a>
                          <span className="text-sm text-muted-foreground">{formatStamp(run.at)}</span>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </Section>
            </div>
          </>
        );
      }}
    </Loaded>
  );
}
