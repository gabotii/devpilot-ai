import { Link } from 'react-router';
import { useDashboard } from '../api/hooks';
import { useAuth, useOrg } from '../app/providers';
import { BarList, TrendChart } from '../components/charts';
import { ActivityList } from '../components/ActivityList';
import { Card, ErrorState, LoadingState } from '../components/ui';
import { formatDateTime, priorityColor, priorityLabel } from '../lib/format';
import { OperationsPanels } from './operations/OperationsPanels';

const weekFormat = new Intl.DateTimeFormat('es', { day: 'numeric', month: 'short' });
const formatWeek = (iso: string) => weekFormat.format(new Date(`${iso}T00:00:00`));

function Stat({ label, value, hint, to }: { label: string; value: number; hint?: string; to?: string }) {
  const content = (
    <div className="card stat">
      <div className="stat-label">{label}</div>
      <div className="stat-value">{value}</div>
      {hint ? <div className="stat-hint">{hint}</div> : null}
    </div>
  );
  return to ? (
    <Link to={to} style={{ color: 'inherit', textDecoration: 'none' }} aria-label={`${label}: ${value}`}>
      {content}
    </Link>
  ) : (
    content
  );
}

export function DashboardPage() {
  const me = useAuth();
  const { orgId, org, path } = useOrg();
  const dashboard = useDashboard(orgId);

  if (dashboard.isPending) return <LoadingState label="Cargando dashboard…" />;
  if (dashboard.error) return <ErrorState error={dashboard.error} onRetry={() => void dashboard.refetch()} />;

  const d = dashboard.data;
  const w = d.work;
  const isEmpty = w.activeProjects === 0 && w.openItems === 0;

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Dashboard</h1>
          <p>
            {org.name} · datos en tiempo real, actualizados {formatDateTime(d.generatedAt)}
          </p>
        </div>
        <Link className="btn btn-primary" to={path('/projects')}>
          Ver proyectos
        </Link>
      </div>

      {isEmpty ? (
        <div className="alert alert-info">
          Aún no hay trabajo registrado. Crea tu primer proyecto para empezar, {me.user.displayName.split(' ')[0]}.
        </div>
      ) : null}

      <div className="grid grid-stats">
        <Stat label="Proyectos activos" value={w.activeProjects} to={path('/projects')} />
        <Stat label="Tareas abiertas" value={w.openItems} hint={`${w.inProgressItems} en curso`} />
        <Stat label="Asignadas a mí" value={w.myOpenItems} />
        <Stat label="Incidencias abiertas" value={w.openIssues} to={path('/incidents')} />
        <Stat label="Vencidas" value={w.overdueItems} />
        <Stat label="Completadas (30 días)" value={w.completedLast30Days} />
      </div>

      <div className="grid grid-2">
        <Card title="Tendencia semanal" description="Tareas creadas y completadas por semana (últimas 8 semanas).">
          <TrendChart
            data={d.completionTrend.map((t) => ({ week: t.weekStart, created: t.created, completed: t.completed }))}
            xKey="week"
            xFormat={formatWeek}
            series={[
              { key: 'created', label: 'Creadas', color: 'var(--chart-1)' },
              { key: 'completed', label: 'Completadas', color: 'var(--chart-2)' },
            ]}
            caption="Tareas creadas y completadas por semana"
          />
        </Card>
        <Card title="Trabajo abierto por prioridad">
          <BarList
            caption="Tareas abiertas por prioridad"
            rows={d.openByPriority.map((p) => ({
              key: p.priority,
              value: p.count,
              label: (
                <span className="priority">
                  <span className="dot" style={{ background: priorityColor[p.priority] }} aria-hidden />
                  {priorityLabel[p.priority]}
                </span>
              ),
            }))}
          />
        </Card>
      </div>

      <OperationsPanels />

      <Card title="Actividad reciente" actions={<Link to={path('/activity')}>Ver todo</Link>}>
        <ActivityList items={d.recentActivity} />
      </Card>
    </div>
  );
}
