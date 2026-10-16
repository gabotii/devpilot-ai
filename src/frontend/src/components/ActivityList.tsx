import type { Activity } from '../api/types';
import { formatRelative } from '../lib/format';
import { Avatar, EmptyState } from './ui';

export function ActivityList({ items }: { items: Activity[] }) {
  if (items.length === 0) return <EmptyState title="Sin actividad todavía" description="Las acciones del equipo aparecerán aquí." />;
  return (
    <ul className="timeline">
      {items.map((a) => (
        <li key={a.id}>
          <Avatar name={a.actor?.displayName ?? 'Sistema'} small />
          <div style={{ minWidth: 0 }}>
            <div>
              <strong>{a.actor?.displayName ?? 'Sistema'}</strong> {a.summary}
            </div>
            <time className="muted small" dateTime={a.at} title={new Date(a.at).toLocaleString('es')}>
              {formatRelative(a.at)}
            </time>
          </div>
        </li>
      ))}
    </ul>
  );
}
