import { useTranslation } from 'react-i18next';
import {
  PictureButton,
  type PictureProps,
  ProgressText,
  tapHandler,
  usePictureText,
} from './shared';

const logs: readonly (readonly [x: number, y: number, width: number, turn: number])[] = [
  [30, 112, 80, -8],
  [30, 112, 80, 8],
  [42, 100, 56, 0],
];

/** The fire at `count` of `target`: the flame grows and a log joins it as check-ins add up. */
function KindlingDrawing({ count, target }: { count: number; target: number }) {
  const lit = count === 0 ? 0 : Math.ceil((logs.length * count) / target);
  return (
    <svg viewBox="0 0 140 140" aria-hidden="true" className="h-16 w-auto">
      <g
        className="picture-move picture-grow"
        style={{ transform: `scale(${0.35 + (0.65 * count) / target})` }}
      >
        <path
          d="M70 20C84 44 104 58 100 86C97 106 84 116 70 116S43 106 40 86C37 62 58 52 70 20z"
          fill="var(--flame)"
        />
        <path
          d="M70 60C78 74 88 82 86 96C84 106 78 112 70 112S56 106 54 96C52 84 64 76 70 60z"
          fill="var(--flame-core)"
        />
      </g>
      {logs.map(([x, y, width, turn], i) => (
        <g key={i} transform={`rotate(${turn} 70 ${y + 6})`}>
          <rect
            x={x}
            y={y}
            width={width}
            height="14"
            rx="7"
            fill="var(--card)"
            stroke="var(--empty)"
            strokeWidth="2.5"
          />
          <g className="picture-move" style={{ opacity: i < lit ? 1 : 0 }}>
            <rect x={x} y={y} width={width} height="14" rx="7" fill="var(--picture-wood)" />
            <circle cx={x + width - 7} cy={y + 7} r="5" fill="var(--picture-wood-end)" />
          </g>
        </g>
      ))}
    </svg>
  );
}

/** Kindling, Ember's own picture for any counted habit: one tap is one check-in. */
export function Kindling(props: PictureProps) {
  const { habit, count, busy, animate } = props;
  const { t } = useTranslation();
  const { progress, action } = usePictureText(habit, count, t('habitVisuals.today.kindling.add'));
  const target = habit.timesPerDay;
  return (
    <div
      className="flex flex-wrap items-center gap-x-2 gap-y-2"
      data-picture-motion={animate ? '' : undefined}
    >
      <PictureButton
        label={t('habitVisuals.today.kindling.label', {
          name: habit.name,
          done: count,
          count: target,
          action,
        })}
        busy={busy}
        onClick={tapHandler(props)}
      >
        <KindlingDrawing count={count} target={target} />
      </PictureButton>
      <ProgressText text={progress} />
    </div>
  );
}
