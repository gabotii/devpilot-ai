import { useState } from 'react';
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';

export interface SeriesDef {
  key: string;
  label: string;
  color: string;
}

interface TrendChartProps {
  data: Record<string, string | number>[];
  xKey: string;
  xFormat: (value: string) => string;
  series: SeriesDef[];
  caption: string;
}

/**
 * Multi-series line chart: 2px lines, recessive grid, crosshair tooltip listing every series,
 * legend + direct end labels (identity never relies on color alone) and a table view.
 */
export function TrendChart({ data, xKey, xFormat, series, caption }: TrendChartProps) {
  const [showTable, setShowTable] = useState(false);
  const last = data[data.length - 1];

  return (
    <figure style={{ margin: 0 }} className="stack-sm">
      <div className="row-between">
        <div className="legend" aria-hidden>
          {series.map((s) => (
            <span key={s.key}>
              <svg width="16" height="4">
                <line x1="0" y1="2" x2="16" y2="2" stroke={s.color} strokeWidth="2" strokeLinecap="round" />
              </svg>
              {s.label}
              {last ? <strong style={{ color: 'var(--text)' }}>{String(last[s.key])}</strong> : null}
            </span>
          ))}
        </div>
        <button className="btn btn-ghost btn-sm" onClick={() => setShowTable((v) => !v)} aria-expanded={showTable}>
          {showTable ? 'Ver gráfico' : 'Ver tabla'}
        </button>
      </div>
      {showTable ? (
        <div className="table-wrap">
          <table className="table">
            <caption className="sr-only">{caption}</caption>
            <thead>
              <tr>
                <th scope="col">Semana</th>
                {series.map((s) => (
                  <th key={s.key} scope="col">
                    {s.label}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {data.map((row) => (
                <tr key={String(row[xKey])}>
                  <td>{xFormat(String(row[xKey]))}</td>
                  {series.map((s) => (
                    <td key={s.key}>{row[s.key]}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="chart-box" role="img" aria-label={caption}>
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={data} margin={{ top: 8, right: 12, bottom: 0, left: -16 }}>
              <CartesianGrid stroke="var(--chart-grid)" vertical={false} />
              <XAxis
                dataKey={xKey}
                tickFormatter={xFormat}
                tick={{ fill: 'var(--text-3)', fontSize: 12 }}
                axisLine={{ stroke: 'var(--border)' }}
                tickLine={false}
                minTickGap={16}
              />
              <YAxis allowDecimals={false} tick={{ fill: 'var(--text-3)', fontSize: 12 }} axisLine={false} tickLine={false} />
              <Tooltip
                cursor={{ stroke: 'var(--border-strong)', strokeWidth: 1 }}
                content={(props) => (
                  <ChartTooltip active={props.active} payload={props.payload} label={props.label} series={series} xFormat={xFormat} />
                )}
              />
              {series.map((s) => (
                <Line
                  key={s.key}
                  type="monotone"
                  dataKey={s.key}
                  name={s.label}
                  stroke={s.color}
                  strokeWidth={2}
                  dot={false}
                  activeDot={{ r: 4, strokeWidth: 2, stroke: 'var(--surface)' }}
                  isAnimationActive={false}
                />
              ))}
            </LineChart>
          </ResponsiveContainer>
        </div>
      )}
      <figcaption className="sr-only">{caption}</figcaption>
    </figure>
  );
}

interface ChartTooltipProps {
  active?: boolean;
  payload?: readonly { dataKey?: unknown; value?: unknown }[];
  label?: unknown;
  series: SeriesDef[];
  xFormat: (v: string) => string;
}

function ChartTooltip({ active, payload, label, series, xFormat }: ChartTooltipProps) {
  if (!active || !payload?.length) return null;
  return (
    <div className="card" style={{ padding: '8px 12px', boxShadow: 'var(--shadow)' }}>
      <div className="muted small">{xFormat(String(label))}</div>
      {series.map((s) => {
        const entry = payload.find((p) => p.dataKey === s.key);
        return (
          <div key={s.key} className="row" style={{ gap: 8 }}>
            <svg width="12" height="4" aria-hidden>
              <line x1="0" y1="2" x2="12" y2="2" stroke={s.color} strokeWidth="2" strokeLinecap="round" />
            </svg>
            <strong>{String(entry?.value ?? 0)}</strong>
            <span className="muted small">{s.label}</span>
          </div>
        );
      })}
    </div>
  );
}

/** Horizontal magnitude bars in a single hue with direct value labels; the label text carries identity. */
export function BarList({ rows, caption }: { rows: { key: string; label: React.ReactNode; value: number }[]; caption: string }) {
  const max = Math.max(1, ...rows.map((r) => r.value));
  return (
    <div className="bar-list" role="list" aria-label={caption}>
      {rows.map((r) => (
        <div key={r.key} className="bar-row" role="listitem">
          <span className="small">{r.label}</span>
          <div className="bar-track" aria-hidden>
            <div className="bar-fill" style={{ width: `${(r.value / max) * 100}%`, background: 'var(--chart-1)' }} />
          </div>
          <strong className="small" style={{ textAlign: 'right' }}>
            {r.value}
          </strong>
        </div>
      ))}
    </div>
  );
}
