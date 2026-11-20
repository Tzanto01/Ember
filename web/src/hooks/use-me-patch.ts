import { useRef, useState } from 'react';
import type { ApiError } from '@/lib/problem';

interface MePatchFieldOptions<T> {
  /** Reports a failed save; runs only while this value is still the latest one chosen. */
  onError: (error: ApiError) => void;
  /** Applies the value on this device at once; returns how to put the previous one back if the save fails. */
  apply?: (value: T) => () => void;
}

/**
 * One account setting saved through `PATCH /api/me`. Returns the value being saved (`null` once no save is pending,
 * so the caller falls back to `me`) and the change handler. `save` sends the patch; quick successive changes keep the
 * newest choice on screen, and only the newest one clears it, reverts `apply` or reports an error.
 */
export function useMePatchField<T>(
  save: (value: T) => Promise<ApiError | null>,
  { onError, apply }: MePatchFieldOptions<T>,
): readonly [T | null, (value: T) => void] {
  const latest = useRef<object | null>(null);
  const [pending, setPending] = useState<T | null>(null);

  function change(value: T): void {
    const call = {};
    latest.current = call;
    setPending(value);
    const revert = apply?.(value);
    void save(value).then((error) => {
      if (latest.current !== call) return;
      latest.current = null;
      setPending(null);
      if (!error) return;
      revert?.();
      onError(error);
    });
  }

  return [pending, change] as const;
}
