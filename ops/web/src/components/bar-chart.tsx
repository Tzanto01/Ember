import { formatNumber } from '@/lib/format';

export interface Series {
  label: string;
  color: string;
}

export interface Bar {
  label: string;
  values: number[];
}

interface Props {
  title: string;
  note?: string;
  series: Series[];
  bars: Bar[];
}

const height = 120;

/** Stacked bar chart; the SVG stretches to the container, axis labels are HTML so text never scales. */
export function BarChart({ title, note, series, bars }: Props) {
  const totals = bars.map((b) => b.values.reduce((s, v) => s + v, 0));
  const max = Math.max(1, ...totals);
  const sum = totals.reduce((s, v) => s + v, 0);
  const peak = totals.indexOf(Math.max(...totals, 0));
  const summary =
    bars.length === 0
      ? `${title}: no data`
      : `${title}, ${bars[0]!.label} to ${bars[bars.length - 1]!.label}: total ${formatNumber(sum)}` +
        (sum > 0 ? `, peak ${formatNumber(totals[peak]!)} on ${bars[peak]!.label}` : '');
  const w = 100 / Math.max(1, bars.length);
  const mid = bars[Math.floor((bars.length - 1) / 2)];

  return (
    <figure className="m-0 flex flex-col gap-2">
      <figcaption>
        <span className="font-bold">{title}</span>
        {note && <span className="block text-sm text-muted-foreground">{note}</span>}
      </figcaption>
      {series.length > 1 && (
        <ul className="m-0 flex list-none flex-wrap gap-3 p-0 text-sm text-muted-foreground" aria-hidden="true">
          {series.map((s) => (
            <li key={s.label} className="flex items-center gap-1.5">
              <span className="inline-block size-3 rounded-sm" style={{ background: s.color }} />
              {s.label}
            </li>
          ))}
        </ul>
      )}
      <div className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-2 text-xs text-muted-foreground tabular-nums">
        <div className="flex flex-col justify-between text-right" aria-hidden="true" style={{ height }}>
          <span>{formatNumber(max)}</span>
          <span>0</span>
        </div>
        <svg
          role="img"
          aria-label={summary}
          viewBox={`0 0 100 ${height}`}
          preserveAspectRatio="none"
          className="block w-full border-b border-l border-border"
          style={{ height }}
        >
          {bars.map((b, i) => {
            let y = height;
            return (
              <g key={b.label + i}>
                <title>{`${b.label}: ${b.values.map((v, j) => (series.length > 1 ? `${series[j]!.label} ${v}` : v)).join(', ')}`}</title>
                <rect x={i * w} y={0} width={w} height={height} fill="transparent" />
                {b.values.map((v, j) => {
                  const h = (v / max) * (height - 4);
                  y -= h;
                  return (
                    <rect
                      key={j}
                      x={i * w + w * 0.12}
                      y={y}
                      width={w * 0.76}
                      height={h}
                      fill={series[j]!.color}
                    />
                  );
                })}
              </g>
            );
          })}
        </svg>
        <span />
        <div className="flex justify-between" aria-hidden="true">
          <span>{bars[0]?.label}</span>
          {bars.length > 2 && <span>{mid?.label}</span>}
          <span>{bars.length > 1 ? bars[bars.length - 1]!.label : ''}</span>
        </div>
      </div>
    </figure>
  );
}
