import { useTranslation } from 'react-i18next';
import {
  PictureButton,
  type PictureProps,
  ProgressText,
  tapHandler,
  usePictureText,
} from './shared';

const step = 20;

/** The trail at `count` of `target`: a stop per check-in, walked from the start toward the flag. */
function TrailDrawing({ count, target }: { count: number; target: number }) {
  const stops = Array.from({ length: target + 1 }, (_, i) => ({
    x: 14 + i * step,
    y: i % 2 === 0 ? 48 : 30,
  }));
  const path = stops
    .map((stop, i) => {
      if (i === 0) return `M${stop.x} ${stop.y}`;
      const from = stops[i - 1]!;
      return `C${from.x + step / 2} ${from.y} ${stop.x - step / 2} ${stop.y} ${stop.x} ${stop.y}`;
    })
    .join('');
  const end = stops[target]!;
  const here = stops[count]!;
  return (
    <svg viewBox={`0 0 ${end.x + 26} 64`} aria-hidden="true" className="h-16 w-auto">
      <path d={path} fill="none" stroke="var(--empty)" strokeWidth="7" strokeLinecap="round" />
      <path
        d={path}
        pathLength={1}
        fill="none"
        stroke="var(--done)"
        strokeWidth="7"
        strokeLinecap="round"
        strokeDasharray="1 1"
        className="picture-move"
        style={{ strokeDashoffset: 1 - count / target, opacity: count === 0 ? 0 : 1 }}
      />
      {stops.map((stop, i) => (
        <circle
          key={i}
          cx={stop.x}
          cy={stop.y}
          r="4.5"
          fill={i <= count ? 'var(--done)' : 'var(--card)'}
          stroke={i <= count ? 'var(--done)' : 'var(--empty)'}
          strokeWidth="2.5"
        />
      ))}
      <path
        d={`M${end.x} ${end.y - 5}V${end.y - 27}l16 6-16 6`}
        fill="var(--flame)"
        stroke="var(--primary)"
        strokeWidth="2"
        strokeLinejoin="round"
      />
      <circle
        r="6.5"
        fill="var(--primary)"
        stroke="var(--card)"
        strokeWidth="2"
        className="picture-move"
        style={{ transform: `translate(${here.x}px, ${here.y}px)` }}
      />
    </svg>
  );
}

/** The trail for walks and runs: one tap walks on to the next stop. */
export function Trail(props: PictureProps) {
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
        label={t('habitVisuals.today.trail.label', {
          name: habit.name,
          done: count,
          count: target,
          action,
        })}
        busy={busy}
        onClick={tapHandler(props)}
      >
        <TrailDrawing count={count} target={target} />
      </PictureButton>
      <ProgressText text={progress} />
    </div>
  );
}
