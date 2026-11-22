/** The ring that briefly marks an element: a habit row after a reminder tap opened Today, or a guide "Show me" target. */
export const highlightRing = 'ring-2 ring-primary ring-offset-2 ring-offset-background';

/** How long {@link highlightRing} stays on, in milliseconds. */
export const highlightMs = 1500;

/**
 * The element a guide target names: the one with that id, else the first shown element with that `data-guide` value
 * that isn't pressed (an open check-in before a done one), else the first shown one with that value; none inside a
 * closed list (`[hidden]`).
 */
export function findGuideTarget(target: string): HTMLElement | null {
  const shown = [...document.querySelectorAll<HTMLElement>(`[data-guide="${target}"]`)].filter(
    (element) => !element.closest('[hidden]'),
  );
  return (
    document.getElementById(target) ??
    shown.find((element) => element.getAttribute('aria-pressed') !== 'true') ??
    shown[0] ??
    null
  );
}

/** Scrolls `element` to the middle of the screen (at once when `instant`) and rings it for {@link highlightMs}. */
export function showTarget(element: HTMLElement, instant: boolean): void {
  element.scrollIntoView({ block: 'center', behavior: instant ? 'auto' : 'smooth' });
  const ring = highlightRing.split(' ');
  element.classList.add(...ring);
  setTimeout(() => element.classList.remove(...ring), highlightMs);
}
