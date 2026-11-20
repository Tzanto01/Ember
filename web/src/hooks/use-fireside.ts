import { useQuery } from '@tanstack/react-query';
import { firesideApi, firesideAvailable, firesideKey } from '@/lib/fireside';
import { auth, localMode } from '@/lib/services';
import { useStore } from '@/lib/store';

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
    staleTime: 30_000,
    refetchInterval: (q) => (q.state.data?.code && Date.parse(q.state.data.code.expiresAt) > Date.now() ? 60_000 : false),
    refetchOnWindowFocus: true,
  });
  return { ...query, available, data: available ? query.data : undefined };
}
