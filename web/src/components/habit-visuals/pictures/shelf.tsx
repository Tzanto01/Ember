import { useTranslation } from 'react-i18next';
import {
  PictureButton,
  type PictureProps,
  ProgressText,
  tapHandler,
  usePictureText,
} from './shared';

const widths = [18, 14, 20, 16];
const heights = [64, 78, 56, 70];
const gap = 5;

/** The shelf at `count` of `target`: one book stands in its place per check-in; open places are dashed outlines. */
function ShelfDrawing({ count, target }: { count: number; target: number }) {
  const books = Array.from({ length: target }, (_, i) => ({
    x:
      12 +
      Array.from({ length: i }, (_, j) => widths[j % widths.length]! + gap).reduce(
        (a, b) => a + b,
        0,
      ),
    width: widths[i % widths.length]!,
    height: heights[i % heights.length]!,
  }));
  const last = books[books.length - 1]!;
  const end = last.x + last.width + gap + 2;
  return (
    <svg viewBox={`0 0 ${end + 28} 120`} aria-hidden="true" className="h-16 w-auto">
      {books.map((book, i) => {
        const top = 100 - book.height;
        return (
          <g key={i}>
            <rect
              x={book.x + 1}
              y={top + 1}
              width={book.width - 2}
              height={book.height - 2}
              rx="3"
              fill="none"
              stroke="var(--muted-foreground)"
              strokeWidth="2"
              strokeDasharray="4 4"
            />
            <g
              className="picture-move"
              style={{
                opacity: i < count ? 1 : 0,
                transform: i < count ? 'none' : 'translateY(-8px)',
              }}
            >
              <rect
                x={book.x}
                y={top}
                width={book.width}
                height={book.height}
                rx="3"
                fill={`var(--picture-book-${(i % 4) + 1})`}
              />
              <rect
                x={book.x + 4}
                y={top + 10}
                width={book.width - 8}
                height="3"
                rx="1.5"
                fill="var(--card)"
                opacity="0.7"
              />
              <rect
                x={book.x + 4}
                y={top + 16}
                width={book.width - 8}
                height="3"
                rx="1.5"
                fill="var(--card)"
                opacity="0.7"
              />
            </g>
          </g>
        );
      })}
      <rect x="4" y="100" width={end + 20} height="8" rx="4" fill="var(--picture-wood)" />
      <g transform={`translate(${end + 12} 72)`}>
        <rect x="-8" y="14" width="16" height="14" rx="3" fill="var(--picture-wood-end)" />
        <path d="M0 14C-10 6-8-6 0-10C8-6 10 6 0 14z" fill="var(--picture-leaf)" />
      </g>
    </svg>
  );
}

/** The shelf for reading habits: one tap is one session, and a book takes its place. */
export function Shelf(props: PictureProps) {
  const { habit, count, busy, animate } = props;
  const { t } = useTranslation();
  const { progress, action } = usePictureText(habit, count);
  const target = habit.timesPerDay;
  return (
    <div
      className="flex flex-wrap items-center gap-x-2 gap-y-2"
      data-picture-motion={animate ? '' : undefined}
    >
      <PictureButton
        label={t('habitVisuals.today.shelf.label', {
          name: habit.name,
          done: count,
          count: target,
          action,
        })}
        busy={busy}
        onClick={tapHandler(props)}
      >
        <ShelfDrawing count={count} target={target} />
      </PictureButton>
      <ProgressText text={progress} />
    </div>
  );
}
