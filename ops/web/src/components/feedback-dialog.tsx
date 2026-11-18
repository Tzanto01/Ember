import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { X } from 'lucide-react';
import { type FormEvent, useEffect, useRef, useState } from 'react';
import { addComment, commentsQuery, feedbackQuery, imageUrl } from '@/api/client';
import type { FeedbackItem } from '@/api/types';
import { Badge } from '@/components/ui';
import { capitalize, formatStamp } from '@/lib/format';

const maxComment = 4000;

export function FeedbackDialog({ item, onClose }: { item: FeedbackItem; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const queryClient = useQueryClient();
  const comments = useQuery(commentsQuery(item.id));
  const [body, setBody] = useState('');
  const [invalid, setInvalid] = useState(false);
  const post = useMutation({
    mutationFn: (text: string) => addComment(item.id, text),
    onSuccess: () => {
      setBody('');
      void queryClient.invalidateQueries({ queryKey: commentsQuery(item.id).queryKey });
      void queryClient.invalidateQueries({ queryKey: feedbackQuery.queryKey });
    },
  });

  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
  }, []);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const text = body.trim();
    if (!text) {
      setInvalid(true);
      return;
    }
    setInvalid(false);
    post.mutate(text);
  };

  const titleId = `dialog-${item.id}-title`;
  const errorId = `dialog-${item.id}-error`;
  return (
    <dialog
      ref={ref}
      aria-labelledby={titleId}
      onClose={onClose}
      onClick={(e) => e.target === ref.current && ref.current.close()}
      className="m-auto max-h-[92dvh] w-[min(46rem,calc(100vw-1.5rem))] rounded-lg border-0 bg-card p-0 text-card-foreground shadow-card"
    >
      <div className="flex flex-col gap-4 p-4 sm:p-6">
        <div className="flex items-start gap-3">
          <h2 id={titleId} className="m-0 flex-1 text-xl">
            {item.title}
          </h2>
          <button type="button" className="btn btn-sm" onClick={() => ref.current?.close()}>
            <X aria-hidden="true" className="size-4" />
            Close
          </button>
        </div>
        <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
          <Badge tone="warn">{capitalize(item.status)}</Badge>
          <Badge>{capitalize(item.platform)}</Badge>
          {item.version && <span>v{item.version}</span>}
          {item.anonymous && <Badge tone="warn">Anonymous</Badge>}
          <span>
            sent <time dateTime={item.createdAt}>{formatStamp(item.createdAt)}</time>
          </span>
          {item.resolvedAt && (
            <span>
              resolved <time dateTime={item.resolvedAt}>{formatStamp(item.resolvedAt)}</time>
            </span>
          )}
        </div>
        <p className="m-0 whitespace-pre-wrap">{item.text}</p>
        {item.imageIds.length > 0 && (
          <section aria-label="Images">
            <ul className="m-0 grid list-none grid-cols-2 gap-2 p-0 sm:grid-cols-3">
              {item.imageIds.map((imageId, i) => (
                <li key={imageId}>
                  <a href={imageUrl(item.id, imageId)} target="_blank" rel="noreferrer noopener" className="block rounded-md">
                    <img
                      src={imageUrl(item.id, imageId)}
                      alt={`Image ${i + 1} of ${item.imageIds.length} (opens in a new tab)`}
                      loading="lazy"
                      className="block aspect-square w-full rounded-md bg-muted object-cover"
                    />
                  </a>
                </li>
              ))}
            </ul>
          </section>
        )}
        <section aria-labelledby={`${titleId}-comments`} className="flex flex-col gap-3">
          <h3 id={`${titleId}-comments`} className="m-0 text-lg">
            Comments
          </h3>
          {comments.isPending ? (
            <p role="status" className="m-0 text-muted-foreground">
              Loading comments…
            </p>
          ) : comments.isError ? (
            <p role="alert" className="m-0">
              Could not load comments.
            </p>
          ) : comments.data.length === 0 ? (
            <p className="m-0 text-muted-foreground">No comments yet.</p>
          ) : (
            <ol className="m-0 flex list-none flex-col gap-2 p-0">
              {comments.data.map((c) => (
                <li key={c.id} className="rounded-md bg-muted p-3">
                  <p className="m-0 text-sm text-muted-foreground">
                    <span className="font-bold text-foreground">{c.author}</span>{' '}
                    <time dateTime={c.createdAt}>{formatStamp(c.createdAt)}</time>
                  </p>
                  <p className="m-0 whitespace-pre-wrap">{c.body}</p>
                </li>
              ))}
            </ol>
          )}
          <form onSubmit={submit} className="flex flex-col gap-2" noValidate>
            <label htmlFor={`${titleId}-new`} className="font-bold">
              Add a comment
            </label>
            <textarea
              id={`${titleId}-new`}
              value={body}
              maxLength={maxComment}
              rows={3}
              aria-invalid={invalid || undefined}
              aria-describedby={invalid || post.isError ? errorId : undefined}
              onChange={(e) => setBody(e.target.value)}
            />
            {(invalid || post.isError) && (
              <p id={errorId} role="alert" className="m-0 text-sm font-bold text-destructive">
                {invalid ? 'Write a comment first.' : `Could not save the comment: ${post.error?.message}`}
              </p>
            )}
            <div>
              <button type="submit" className="btn btn-primary" disabled={post.isPending}>
                {post.isPending ? 'Saving…' : 'Add comment'}
              </button>
            </div>
          </form>
        </section>
      </div>
    </dialog>
  );
}
