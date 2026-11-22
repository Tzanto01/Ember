import { LiveRegion } from '@/components/live-region';
import { momentCopy } from '@/lib/moment/copy';

/** Longest text a reflect-step field takes. */
export const maxReflectText = 300;

const announceFrom = 20;

/**
 * The "N characters left" line under a reflect-step field, with `id` for the field's `aria-describedby`. It is
 * announced only at the threshold and at the limit.
 */
export function CharactersLeft({ id, text }: { id: string; text: string }) {
  const left = maxReflectText - text.length;
  return (
    <>
      <p id={id} className="self-end text-[0.875rem] text-muted-foreground">
        {momentCopy.charactersLeft(left)}
      </p>
      <LiveRegion
        message={
          left === 0
            ? momentCopy.limitReached
            : left <= announceFrom
              ? momentCopy.charactersLeft(announceFrom)
              : ''
        }
      />
    </>
  );
}
