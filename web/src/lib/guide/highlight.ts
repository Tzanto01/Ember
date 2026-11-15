/** The ring that briefly marks an element: a habit row after a reminder tap opened Today, or a guide "Show me" target. */
export const highlightRing = 'ring-2 ring-primary ring-offset-2 ring-offset-background';

/** How long {@link highlightRing} stays on, in milliseconds. */
export const highlightMs = 1500;

/** The element a guide target names: the one with that id, else the first with that `data-guide` value. */
export function findGuideTarget(target: string): HTMLElement | null {
  return document.getElementById(target) ?? document.querySelector<HTMLElement>(`[data-guide="${target}"]`);
}

/** Scrolls `element` to the middle of the screen (at once when `instant`) and rings it for {@link highlightMs}. */
export function showTarget(element: HTMLElement, instant: boolean): void {
  element.scrollIntoView({ block: 'center', behavior: instant ? 'auto' : 'smooth' });
  const ring = highlightRing.split(' ');
  element.classList.add(...ring);
  setTimeout(() => element.classList.remove(...ring), highlightMs);
}
