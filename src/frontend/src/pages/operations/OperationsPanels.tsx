import { Link } from 'react-router';
import { AlertTriangle, Bot, Plug } from 'lucide-react';
import { useAiStatus, useOperationsSummary } from '../../api/opsHooks';
import { useOrg } from '../../app/providers';
import { Badge, Card, EmptyState, ErrorState, LoadingState } from '../../components/ui';
import { formatDuration, formatRelative } from '../../lib/format';
import { DeploymentBadge, RunBadge } from './opsUi';

function pct(value: number | null) {
  return value === null ? '—' : `${Math.round(value * 100)}%`;
}

/** Operational section of the dashboard. Every value comes from recorded webhook/ingestion events; nothing is simulated. */
export function OperationsPanels() {
  const { orgId, path, hasRole } = useOrg();
  const summary = useOperationsSummary(orgId);
  const ai = useAiStatus(orgId);

  if (summary.isPending) return <LoadingState label="Cargando operaciones…" />;
  if (summary.error) return <ErrorState error={summary.error} onRetry={() => void summary.refetch()} />;
  const s = summary.data;

  const alerts: { tone: 'danger' | 'warning'; text: string; to: string }[] = [];
  if (s.criticalOpenIncidents > 0) alerts.push({ tone: 'danger', text: `${s.criticalOpenIncidents} incidente(s) crítico(s) abierto(s)`, to: path('/incidents') });
  if (s.integrationsWithErrors > 0) alerts.push({ tone: 'warning', text: `${s.integrationsWithErrors} integración(es) con errores en las últimas 24 h`, to: path('/integrations') });
  if (s.pendingRepositories > 0 && hasRole('Admin'))
    alerts.push({ tone: 'warning', text: `${s.pendingRepositories} repositorio(s) pendientes de autorizar`, to: path('/integrations') });
  if (ai.data && !ai.data.reachable) alerts.push({ tone: 'warning', text: 'El servicio de IA no está disponible (el resto de la plataforma funciona con normalidad)', to: path('/assistant') });

  return (
    <div className="stack">
      {alerts.length > 0 ? (
        <section className="stack-sm" aria-label="Alertas">
          {alerts.map((a) => (
            <Link key={a.text} to={a.to} className={`alert alert-${a.tone}`} style={{ textDecoration: 'none' }}>
              <AlertTriangle size={16} aria-hidden style={{ marginTop: 2 }} />
              <span>{a.text}</span>
            </Link>
          ))}
        </section>
      ) : null}

      <div className="grid grid-stats">
        <div className="card stat">
          <div className="stat-label">Ejecuciones (7 días)</div>
          <div className="stat-value">{s.runs7d}</div>
          <div className="stat-hint">{s.failedRuns7d} fallidas</div>
        </div>
        <div className="card stat">
          <div className="stat-label">Tasa de éxito CI</div>
          <div className="stat-value">{pct(s.successRate7d)}</div>
          <div className="stat-hint">Duración media {formatDuration(s.avgDurationSeconds7d)}</div>
        </div>
        <div className="card stat">
          <div className="stat-label">Despliegues (7 días)</div>
          <div className="stat-value">{s.deployments7d}</div>
          <div className="stat-hint">{s.failedDeployments7d} fallidos</div>
        </div>
        <Link to={path('/incidents')} className="card stat" style={{ color: 'inherit', textDecoration: 'none' }}>
          <div className="stat-label">Incidentes abiertos</div>
          <div className="stat-value">{s.openIncidents}</div>
          <div className="stat-hint">{s.criticalOpenIncidents} críticos</div>
        </Link>
      </div>

      {!s.hasOperationalData ? (
        <Card>
          <EmptyState
            icon={<Plug size={28} aria-hidden />}
            title="Sin datos de CI/CD todavía"
            description="Conecta GitHub o envía eventos desde tu CI con una clave de ingesta para ver pipelines y despliegues reales aquí."
            action={
              <Link className="btn" to={path('/integrations')}>
                Configurar integraciones
              </Link>
            }
          />
        </Card>
      ) : (
        <div className="grid grid-2">
          <Card title="Últimos despliegues" actions={<Link to={path('/deployments')}>Ver todos</Link>} bodyClassName="">
            {s.recentDeployments.length === 0 ? (
              <p className="muted card-body">Sin despliegues registrados.</p>
            ) : (
              <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
                {s.recentDeployments.map((d) => (
                  <li key={d.id} className="row-between" style={{ padding: '10px 20px', borderBottom: '1px solid var(--border)' }}>
                    <div style={{ minWidth: 0 }}>
                      <strong>{d.environment}</strong> <span className="muted small mono">{d.ref ?? d.commitSha?.slice(0, 7)}</span>
                      <div className="muted small truncate">{d.repositoryFullName ?? (d.source === 'Api' ? 'API de ingesta' : '')} · {formatRelative(d.createdAt)}</div>
                    </div>
                    <DeploymentBadge status={d.status} />
                  </li>
                ))}
              </ul>
            )}
          </Card>
          <Card title="Salud de pipelines (7 días)" actions={<Link to={path('/deployments?tab=pipelines')}>Ver ejecuciones</Link>} bodyClassName="">
            {s.pipelines.length === 0 ? (
              <p className="muted card-body">Sin ejecuciones en los últimos 7 días.</p>
            ) : (
              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th scope="col">Pipeline</th>
                      <th scope="col">Éxito</th>
                      <th scope="col">Fallos</th>
                      <th scope="col">Último</th>
                    </tr>
                  </thead>
                  <tbody>
                    {s.pipelines.map((p) => (
                      <tr key={p.pipelineId}>
                        <td>{p.name}</td>
                        <td>{pct(p.successRate)}</td>
                        <td>{p.failures}</td>
                        <td>{p.lastConclusion ? <RunBadge status="Completed" conclusion={p.lastConclusion} /> : '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Card>
        </div>
      )}

      <Card title="Estado de servicios">
        <div className="row" style={{ gap: 24 }}>
          <span className="row" style={{ gap: 6 }}>
            <Badge tone="success">API</Badge> operativa
          </span>
          <span className="row" style={{ gap: 6 }}>
            <Bot size={16} aria-hidden />
            {ai.isPending ? (
              'Comprobando IA…'
            ) : ai.data?.reachable ? (
              <>
                <Badge tone="success">Servicio IA</Badge>
                {ai.data.mode === 'deterministic' ? 'modo determinista (sin modelo configurado)' : `modelo ${ai.data.model}`}
              </>
            ) : (
              <>
                <Badge tone="danger">Servicio IA</Badge> no disponible
              </>
            )}
          </span>
          <span className="row" style={{ gap: 6 }}>
            <Plug size={16} aria-hidden /> {s.activeIntegrations} integración(es) activa(s)
          </span>
        </div>
      </Card>
    </div>
  );
}
