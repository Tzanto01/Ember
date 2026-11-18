import { type QueryClient, useQuery } from '@tanstack/react-query';
import { firesideLimits } from './fireside-limits';
import { t } from './i18n';
import type { Me } from './models';
import { auth, http, localMode } from './services';
import { useStore } from './store';

/** The five fixed warmth messages (contract v1.22). */
export const warmthMessages = ['thinking', 'proud', 'easy', 'notAlone', 'blanket'] as const;

export type WarmthMessage = (typeof warmthMessages)[number];

/** A friend at the caller's fireside; `id` is the caller's link id, never a user id. */
export interface FiresideFriend {
  id: number;
  name: string;
  asking: boolean;
  /** Null when that friend doesn't share their flame with the caller. */
  checkedInToday: boolean | null;
  muted: boolean;
  shareFlame: boolean;
  renamedFrom: string | null;
  joinedViaMyCode: boolean;
  canSendWarmth: boolean;
}

/** Undismissed warmth from a friend who isn't muted. */
export interface FiresideWarmth {
  id: number;
  friendId: number;
  name: string;
  message: WarmthMessage;
  sentAt: string;
}

/** The caller's active code. */
export interface FiresideCode {
  code: string;
  expiresAt: string;
}

/** `GET /api/fireside`. */
export interface Fireside {
  enabled: boolean;
  name: string | null;
  notify: boolean;
  askingUntil: string | null;
  code: FiresideCode | null;
  friends: FiresideFriend[];
  warmth: FiresideWarmth[];
}

/** `PUT /api/fireside` body. */
export interface FiresidePut {
  enabled: boolean;
  name?: string;
  notify?: boolean;
}

/** Most characters a Fireside name may have after trimming. */
export const firesideNameMax = firesideLimits.name;

/** Characters in a code, spaces left out. */
export const firesideCodeLength = firesideLimits.codeLength;

const friends = (id: number) => `/api/fireside/friends/${id}`;

/** One method per Fireside endpoint (contract v1.22); each rejects with an `ApiError`. */
export const firesideApi = {
  get: () => http.request<Fireside>('GET', '/api/fireside'),
  put: (body: FiresidePut) => http.request<Fireside>('PUT', '/api/fireside', { body }),
  makeCode: () => http.request<FiresideCode>('POST', '/api/fireside/code'),
  join: (code: string) => http.request<FiresideFriend>('POST', '/api/fireside/join', { body: { code } }),
  sendWarmth: (id: number, message: WarmthMessage) =>
    http.request<void>('POST', `${friends(id)}/warmth`, { body: { message } }),
  patchFriend: (id: number, body: { muted?: boolean; shareFlame?: boolean }) =>
    http.request<FiresideFriend>('PATCH', friends(id), { body }),
  acknowledge: (id: number, body: { rename?: boolean; joined?: boolean }) =>
    http.request<FiresideFriend>('POST', `${friends(id)}/acknowledge`, { body }),
  remove: (id: number, revokeCode: boolean) =>
    http.request<void>('DELETE', friends(id), { params: { revokeCode: String(revokeCode) } }),
  dismiss: (id: number) => http.request<void>('POST', `/api/fireside/warmth/${id}/dismiss`),
  ask: () => http.request<{ askingUntil: string }>('PUT', '/api/fireside/ask'),
  stopAsking: () => http.request<void>('DELETE', '/api/fireside/ask'),
};

export const firesideKey = ['fireside'] as const;

/** True when Fireside can be offered: an account (not no-account mode) with current consent. */
export function firesideAvailable(me: Me | null, mode: string): boolean {
  return (
    mode !== 'local' &&
    !!me &&
    me.consent.given &&
    me.consent.policyVersion === me.consent.currentPolicyVersion
  );
}

/**
 * The caller's Fireside; idle in no-account mode, without an account or before consent. With `cacheOnly` it never
 * fetches and only shows what is already loaded (a moment makes no network call of its own).
 */
export function useFireside({ cacheOnly = false }: { cacheOnly?: boolean } = {}) {
  const me = useStore(auth.me);
  const mode = useStore(localMode.mode);
  const available = firesideAvailable(me, mode);
  const query = useQuery({
    queryKey: firesideKey,
    queryFn: firesideApi.get,
    enabled: available && !cacheOnly,
    gcTime: 60 * 60_000,
    refetchOnWindowFocus: true,
  });
  return { ...query, available, data: available ? query.data : undefined };
}

/** Replaces one friend in the cached Fireside. */
export function putFriend(client: QueryClient, friend: FiresideFriend): void {
  client.setQueryData<Fireside>(firesideKey, (old) =>
    old && {
      ...old,
      friends: old.friends.some((f) => f.id === friend.id)
        ? old.friends.map((f) => (f.id === friend.id ? friend : f))
        : [...old.friends, friend],
    },
  );
}

/** Changes the cached Fireside. */
export function patchFireside(client: QueryClient, change: (old: Fireside) => Fireside): void {
  client.setQueryData<Fireside>(firesideKey, (old) => old && change(old));
}

/** True while the caller's "Could use some warmth today" is showing. */
export function isAsking(fireside: Fireside, now = Date.now()): boolean {
  return !!fireside.askingUntil && Date.parse(fireside.askingUntil) > now;
}

/** A code as shown: `XXXX XXXX`. */
export function formatCode(code: string): string {
  const bare = normalizeCode(code);
  return `${bare.slice(0, 4)} ${bare.slice(4)}`;
}

/** A typed code without spaces, in upper case. */
export function normalizeCode(input: string): string {
  return input.replace(/\s+/g, '').toUpperCase();
}

/** The message text for a warmth key. */
export function warmthText(message: WarmthMessage): string {
  switch (message) {
    case 'thinking':
      return t('fireside.message.thinking');
    case 'proud':
      return t('fireside.message.proud');
    case 'easy':
      return t('fireside.message.easy');
    case 'notAlone':
      return t('fireside.message.notAlone');
    case 'blanket':
      return t('fireside.message.blanket');
  }
}

/** The first letter of a name, for the avatar. */
export function initial(name: string): string {
  return Array.from(name.trim())[0]?.toUpperCase() ?? '?';
}
