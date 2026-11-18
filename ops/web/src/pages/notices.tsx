import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Info } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { deleteNotice, endNotice, errorText, noticesQuery, publishNotice } from '@/api/client';
import type { Notice, NoticeKind, NoticeState, NoticesView } from '@/api/types';
import { Badge, Loaded, Note, PageHeader, Panel } from '@/components/ui';
import { capitalize, formatStamp } from '@/lib/format';
import { codePoints, draftProblem, maxText, noticeId, toUtc } from '@/lib/notices';

const staleUpstream = 60_000;
const kinds: NoticeKind[] = ['info', 'maintenance'];
const stateTone: Record<NoticeState, 'ok' | 'warn' | 'muted'> = { live: 'ok', scheduled: 'warn', ended: 'muted' };

function Stamp({ at }: { at: string }) {
  return <time dateTime={at}>{formatStamp(at)}</time>;
}

function Window({ from, until }: { from?: string; until?: string }) {
  if (from && until)
    return (
      <>
        <Stamp at={from} /> to <Stamp at={until} />
      </>
    );
  if (from)
    return (
      <>
        From <Stamp at={from} />, no end
      </>
    );
  if (until)
    return (
      <>
        Until <Stamp at={until} />
      </>
    );
  return <>No time limit</>;
}

function Preview({ label, lang, text }: { label: string; lang: string; text: string }) {
  const id = `preview-${lang}`;
  return (
    <section aria-labelledby={id} className="flex flex-col gap-1">
      <h3 id={id} className="m-0 text-base">
        {label}
      </h3>
      <div lang={lang} className="flex items-start gap-3 rounded-md border border-border bg-muted px-4 py-3 text-foreground">
        <Info aria-hidden="true" className="mt-[0.1875rem] size-[1.125rem] shrink-0 text-muted-foreground" />
        {text.trim() ? (
          <p className="m-0 min-w-0 flex-1 whitespace-pre-line [overflow-wrap:anywhere]">{text}</p>
        ) : (
          <p className="m-0 flex-1 text-muted-foreground italic">Nothing to preview yet.</p>
        )}
      </div>
    </section>
  );
}

function TextField({ id, label, value, onChange }: { id: string; label: string; value: string; onChange: (v: string) => void }) {
  const count = codePoints(value);
  const over = count > maxText;
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className="font-bold">
        {label}
      </label>
      <textarea
        id={id}
        value={value}
        rows={3}
        aria-invalid={over || undefined}
        aria-describedby={`${id}-count`}
        onChange={(e) => onChange(e.target.value)}
      />
      <p id={`${id}-count`} className={`m-0 text-sm tabular-nums ${over ? 'font-bold text-destructive' : 'text-muted-foreground'}`}>
        {count}/{maxText}
        {over && ` (${count - maxText} too many)`}
      </p>
    </div>
  );
}

export function NoticesPage() {
  const queryClient = useQueryClient();
  const [writtenAt, setWrittenAt] = useState(0);
  const query = useQuery({ ...noticesQuery, refetchInterval: writtenAt ? false : noticesQuery.refetchInterval, refetchOnWindowFocus: !writtenAt });
  const [kind, setKind] = useState<NoticeKind>('info');
  const [en, setEn] = useState('');
  const [de, setDe] = useState('');
  const [from, setFrom] = useState('');
  const [until, setUntil] = useState('');
  const [pending, setPending] = useState<Notice | null>(null);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [status, setStatus] = useState('');
  const [publishError, setPublishError] = useState<string | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const confirmRef = useRef<HTMLHeadingElement>(null);
  const focusAfter = useRef<string | null>(null);

  useEffect(() => {
    if (!writtenAt) return;
    const t = setTimeout(() => setWrittenAt(0), staleUpstream);
    return () => clearTimeout(t);
  }, [writtenAt]);

  useEffect(() => {
    if (pending) confirmRef.current?.focus();
  }, [pending]);

  useEffect(() => {
    const target = focusAfter.current;
    if (!target) return;
    const el = document.querySelector<HTMLButtonElement>(target);
    if (!el || el.disabled) return;
    focusAfter.current = null;
    el.focus();
  });

  const apply = (view: NoticesView) => {
    void queryClient.cancelQueries({ queryKey: noticesQuery.queryKey });
    queryClient.setQueryData(noticesQuery.queryKey, view);
    setWrittenAt(Date.now());
  };

  const publish = useMutation({
    mutationFn: publishNotice,
    onSuccess: (view, notice) => {
      apply(view);
      setPending(null);
      setKind('info');
      setEn('');
      setDe('');
      setFrom('');
      setUntil('');
      setStatus(`Published ${notice.id}.`);
      focusAfter.current = '#notice-en';
    },
    onError: (error) => {
      setPending(null);
      setPublishError(errorText(error));
      focusAfter.current = '#notice-publish';
    },
  });

  const listAction = useMutation({
    mutationFn: ({ id, action }: { id: string; action: 'end' | 'delete' }) => (action === 'end' ? endNotice(id) : deleteNotice(id)),
    onSuccess: (view, { id, action }) => {
      apply(view);
      setDeletingId(null);
      setListError(null);
      setStatus(action === 'end' ? `Ended ${id}.` : `Deleted ${id}.`);
      focusAfter.current = action === 'end' ? `[data-notice-id="${id}"] [data-action="delete"]` : '#notice-status';
    },
    onError: (error, { id, action }) => {
      setDeletingId(null);
      setListError(`Could not ${action === 'end' ? 'end' : 'delete'} ${id}: ${errorText(error)}`);
      focusAfter.current = `[data-notice-id="${id}"] [data-action="${action}"]`;
    },
  });

  const busy = publish.isPending || listAction.isPending;

  return (
    <Loaded query={query} title="Notices">
      {(data) => {
        const problem = draftProblem({ en, de, from, until }, data.notices.length);
        const confirm = () => {
          setPublishError(null);
          setStatus('');
          setPending({
            id: noticeId(en, new Date(), data.notices.map((n) => n.id)),
            kind,
            en: en.trim(),
            de: de.trim(),
            showFrom: toUtc(from),
            showUntil: toUtc(until),
          });
        };
        return (
          <>
            <PageHeader title="Notices" at={query.dataUpdatedAt} fetching={query.isFetching} stale={query.isRefetchError} />
            <p id="notice-status" role="status" tabIndex={-1} className={status ? 'mb-4 font-bold outline-none' : 'sr-only'}>
              {status}
            </p>
            <div className="flex flex-col gap-4">
              {data.publishing ? (
                <div className="grid items-start gap-4 lg:grid-cols-2">
                  <Panel title="New notice">
                    <fieldset disabled={!!pending || publish.isPending} className="m-0 flex min-w-0 flex-col gap-4 border-0 p-0">
                      <fieldset className="m-0 border-0 p-0">
                        <legend className="mb-1 p-0 font-bold">Kind</legend>
                        <div className="inline-flex flex-wrap gap-1 rounded-full bg-muted p-1">
                          {kinds.map((k) => (
                            <label
                              key={k}
                              className="inline-flex min-h-10 cursor-pointer items-center rounded-full px-4 font-bold has-[:checked]:bg-primary has-[:checked]:text-primary-foreground has-[:focus-visible]:outline-3 has-[:focus-visible]:outline-offset-2 has-[:focus-visible]:outline-ring"
                            >
                              <input type="radio" name="notice-kind" value={k} checked={kind === k} onChange={() => setKind(k)} className="sr-only" />
                              {capitalize(k)}
                            </label>
                          ))}
                        </div>
                      </fieldset>
                      <TextField id="notice-en" label="English text" value={en} onChange={setEn} />
                      <TextField id="notice-de" label="German text" value={de} onChange={setDe} />
                      <div className="grid gap-4 sm:grid-cols-2">
                        <label className="flex min-w-0 flex-col gap-1 font-bold">
                          <span>
                            Show from <span className="font-normal text-muted-foreground">(optional, your local time)</span>
                          </span>
                          <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} className="min-w-0 font-normal" />
                        </label>
                        <label className="flex min-w-0 flex-col gap-1 font-bold">
                          <span>
                            Show until <span className="font-normal text-muted-foreground">(optional, your local time)</span>
                          </span>
                          <input type="datetime-local" value={until} onChange={(e) => setUntil(e.target.value)} className="min-w-0 font-normal" />
                        </label>
                      </div>
                    </fieldset>
                    <div className="mt-4 flex flex-col gap-3">
                      {publishError && (
                        <p role="alert" className="m-0 rounded-md bg-notice p-3 text-notice-foreground ring-1 ring-notice-border">
                          Could not publish: {publishError}
                        </p>
                      )}
                      {pending ? (
                        <section aria-labelledby="notice-confirm" className="flex flex-col gap-3 rounded-md bg-muted p-3">
                          <h3 ref={confirmRef} id="notice-confirm" tabIndex={-1} className="m-0 text-base outline-none">
                            Publish this notice to the app?
                          </h3>
                          <dl className="m-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
                            <dt className="text-muted-foreground">Kind</dt>
                            <dd className="m-0 font-semibold">{capitalize(pending.kind)}</dd>
                            <dt className="text-muted-foreground">Shown</dt>
                            <dd className="m-0 font-semibold">
                              <Window from={pending.showFrom} until={pending.showUntil} />
                            </dd>
                            <dt className="text-muted-foreground">Id</dt>
                            <dd className="m-0 font-semibold [overflow-wrap:anywhere]">{pending.id}</dd>
                          </dl>
                          <div className="flex flex-wrap gap-2">
                            <button type="button" className="btn btn-primary" disabled={publish.isPending} onClick={() => publish.mutate(pending)}>
                              {publish.isPending ? 'Publishing…' : 'Publish now'}
                            </button>
                            <button
                              type="button"
                              className="btn"
                              disabled={publish.isPending}
                              onClick={() => {
                                setPending(null);
                                focusAfter.current = '#notice-publish';
                              }}
                            >
                              Cancel
                            </button>
                          </div>
                        </section>
                      ) : (
                        <div className="flex flex-wrap items-center gap-3">
                          <button
                            id="notice-publish"
                            type="button"
                            className="btn btn-primary"
                            disabled={!!problem || busy}
                            aria-describedby={problem ? 'notice-problem' : undefined}
                            onClick={confirm}
                          >
                            Publish
                          </button>
                          {problem && (
                            <p id="notice-problem" className="m-0 text-sm text-muted-foreground">
                              {problem}
                            </p>
                          )}
                        </div>
                      )}
                    </div>
                  </Panel>
                  <Panel title="Preview">
                    <div className="flex flex-col gap-4">
                      <Preview label="Preview (English)" lang="en" text={en} />
                      <Preview label="Preview (German)" lang="de" text={de} />
                    </div>
                  </Panel>
                </div>
              ) : (
                <Note>Publishing isn't set up. The list below is read-only.</Note>
              )}
              <Panel title="Notices in the app">
                {listError && (
                  <div role="alert" className="mb-3 flex flex-wrap items-center gap-3 rounded-md bg-notice p-3 text-notice-foreground ring-1 ring-notice-border">
                    <span className="min-w-0 flex-1">{listError}</span>
                    <button type="button" className="btn btn-sm" onClick={() => setListError(null)}>
                      Dismiss
                    </button>
                  </div>
                )}
                {data.notices.length === 0 ? (
                  <p className="m-0 text-muted-foreground">No notices.</p>
                ) : (
                  <ul className="m-0 flex list-none flex-col gap-3 p-0">
                    {data.notices.map((n) => (
                      <li key={n.id} data-notice-id={n.id} aria-labelledby={`notice-${n.id}`} className="flex flex-col gap-2 rounded-md bg-muted p-3">
                        <div className="flex flex-wrap items-center gap-2">
                          <h3 id={`notice-${n.id}`} className="m-0 min-w-0 text-base [overflow-wrap:anywhere]">
                            {n.id}
                          </h3>
                          <Badge tone={stateTone[n.state]}>{capitalize(n.state)}</Badge>
                          <Badge>{capitalize(n.kind)}</Badge>
                        </div>
                        <p className="m-0 text-sm text-muted-foreground">
                          <Window from={n.showFrom} until={n.showUntil} />
                        </p>
                        <dl className="m-0 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
                          <dt className="text-sm font-bold text-muted-foreground">EN</dt>
                          <dd lang="en" className="m-0 whitespace-pre-line [overflow-wrap:anywhere]">
                            {n.en}
                          </dd>
                          <dt className="text-sm font-bold text-muted-foreground">DE</dt>
                          <dd lang="de" className="m-0 whitespace-pre-line [overflow-wrap:anywhere]">
                            {n.de}
                          </dd>
                        </dl>
                        {data.publishing &&
                          (deletingId === n.id ? (
                            <div className="flex flex-wrap items-center gap-2">
                              <p className="m-0 font-bold">Delete {n.id} for everyone?</p>
                              <button
                                type="button"
                                className="btn btn-primary"
                                disabled={busy}
                                onClick={() => listAction.mutate({ id: n.id, action: 'delete' })}
                              >
                                {listAction.isPending ? 'Deleting…' : 'Yes, delete'}
                              </button>
                              <button
                                type="button"
                                className="btn"
                                autoFocus
                                disabled={busy}
                                onClick={() => {
                                  setDeletingId(null);
                                  focusAfter.current = `[data-notice-id="${n.id}"] [data-action="delete"]`;
                                }}
                              >
                                Cancel
                              </button>
                            </div>
                          ) : (
                            <div className="flex flex-wrap gap-2">
                              {n.state !== 'ended' && (
                                <button
                                  type="button"
                                  className="btn"
                                  data-action="end"
                                  disabled={busy}
                                  onClick={() => listAction.mutate({ id: n.id, action: 'end' })}
                                >
                                  End now<span className="sr-only">: {n.id}</span>
                                </button>
                              )}
                              <button
                                type="button"
                                className="btn"
                                data-action="delete"
                                disabled={busy}
                                onClick={() => {
                                  setListError(null);
                                  setDeletingId(n.id);
                                }}
                              >
                                Delete<span className="sr-only">: {n.id}</span>
                              </button>
                            </div>
                          ))}
                      </li>
                    ))}
                  </ul>
                )}
              </Panel>
            </div>
          </>
        );
      }}
    </Loaded>
  );
}
