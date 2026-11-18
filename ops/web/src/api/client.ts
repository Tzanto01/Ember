import { QueryClient, queryOptions } from '@tanstack/react-query';
import type { Comment, FeedbackList, Overview, Reminders, Session, StatusMove, System } from './types';

export class HttpError extends Error {
  constructor(readonly status: number) {
    super(status === 403 ? 'Access denied (403)' : `Request failed (${status})`);
  }
}

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url, { headers: { Accept: 'application/json' }, credentials: 'same-origin' });
  if (!res.ok) throw new HttpError(res.status);
  return (await res.json()) as T;
}

export const queryClient = new QueryClient({
  defaultOptions: { queries: { refetchOnWindowFocus: true, retry: 1 } },
});

const statsRefetch = 30_000;

export const sessionQuery = queryOptions({ queryKey: ['session'], queryFn: () => getJson<Session>('/api/session'), staleTime: Infinity });
export const overviewQuery = queryOptions({ queryKey: ['overview'], queryFn: () => getJson<Overview>('/api/overview'), refetchInterval: statsRefetch });
export const remindersQuery = queryOptions({ queryKey: ['reminders'], queryFn: () => getJson<Reminders>('/api/reminders'), refetchInterval: statsRefetch });
export const systemQuery = queryOptions({ queryKey: ['system'], queryFn: () => getJson<System>('/api/system'), refetchInterval: statsRefetch });
export const feedbackQuery = queryOptions({ queryKey: ['feedback'], queryFn: () => getJson<FeedbackList>('/api/feedback') });
export const commentsQuery = (id: number) =>
  queryOptions({ queryKey: ['comments', id], queryFn: () => getJson<Comment[]>(`/api/feedback/${id}/comments`) });

async function send(method: 'PUT' | 'POST', url: string, body: unknown, token: string) {
  return fetch(url, {
    method,
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'X-CSRF-TOKEN': token },
    body: JSON.stringify(body),
  });
}

/** Sends a write with the session's CSRF token; on a 400 it refreshes the session once and retries. */
async function write(method: 'PUT' | 'POST', url: string, body: unknown): Promise<Response> {
  let res = await send(method, url, body, (await queryClient.fetchQuery(sessionQuery)).csrfToken);
  if (res.status === 400) {
    const fresh = await queryClient.fetchQuery({ ...sessionQuery, staleTime: 0 });
    res = await send(method, url, body, fresh.csrfToken);
  }
  if (!res.ok) throw new HttpError(res.status);
  return res;
}

export async function moveFeedback(id: number, move: StatusMove): Promise<void> {
  await write('PUT', `/api/feedback/${id}/status`, move);
}

export async function addComment(id: number, body: string): Promise<Comment> {
  return (await (await write('POST', `/api/feedback/${id}/comments`, { body })).json()) as Comment;
}

export const imageUrl = (id: number, imageId: string) => `/api/feedback/${id}/images/${encodeURIComponent(imageId)}`;
