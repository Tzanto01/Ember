import { FlameKindling } from 'lucide-react';
import { useFireside } from '@/hooks/use-fireside';

/** The main-nav entry for `/fireside`. */
export const firesideNavItem = {
  to: '/fireside',
  label: 'fireside.nav',
  icon: FlameKindling,
  under: ['/fireside'],
} as const;

/** `items` with the Fireside entry right after the first one (Today) while Fireside is on. */
export function useNavWithFireside<T>(items: readonly T[]): readonly (T | typeof firesideNavItem)[] {
  const on = useFireside().data?.enabled === true;
  return on ? [...items.slice(0, 1), firesideNavItem, ...items.slice(1)] : items;
}
