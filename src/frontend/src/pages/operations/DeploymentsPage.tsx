import { Link, useSearchParams } from 'react-router';
import { GitCommitHorizontal, GitPullRequest } from 'lucide-react';
import { useCodeEvents, useDeployments, useEnvironments, usePipelineRuns } from '../../api/opsHooks';
import type { DeploymentStatus } from '../../api/opsTypes';
import { useOrg } from '../../app/providers';
import { Badge, EmptyState, ErrorState, LoadingState, Pagination, Select } from '../../components/ui';
import { formatDateTime, formatDuration, formatRelative } from '../../lib/format';
import { DeploymentBadge, ExternalLink, RunBadge } from './opsUi';

const TABS = [
  { id: 'deployments', label: 'Despliegues' },
  { id: 'pipelines', label: 'Ejecuciones de pipelines' },
  { id: 'code', label: 'Commits y pull requests' },
] as const;

export function DeploymentsPage() {
  const [params, setParams] = useSearchParams();
  const tab = params.get('tab') ?? 'deployments';
  const { path } = useOrg();

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Despliegues y pipelines</h1>
          <p>Historial registrado desde GitHub y la API de ingesta. DevPilot observa despliegues; nunca los ejecuta.</p>
        </div>
        <Link className="btn" to={path('/integrations')}>
          Configurar fuentes
        </Link>
      </div>
      <div className="tabs" role="tablist">
        {TABS.map((t) => (
          <button
            key={t.id}
            role="tab"
            aria-selected={tab === t.id}
            className={`tab ${tab === t.id ? 'active' : ''}`}
            style={{ background: 'none', border: 0, cursor: 'pointer', font: 'inherit' }}
            onClick={() => setParams({ tab: t.id }, { replace: true })}
          >
            {t.label}
          </button>
        ))}
      </div>
      {tab === 'pipelines' ? <RunsTable /> : tab === 'code' ? <CodeEvents /> : <DeploymentsTable />}
    </div>
  );
}

function DeploymentsTable() {
  const { orgId } = useOrg();
  const [params, setParams] = useSearchParams();
  const environment = params.get('environment') ?? undefined;
  const status = (params.get('status') as DeploymentStatus | null) ?? undefined;
  const page = Number(params.get('page') ?? '1');
  const deployments = useDeployments(orgId, { environment, status, page });
  const environments = useEnvironments(orgId);
  const set = (k: string, v: string) => {
    const next = new URLSearchParams(params);
    if (v) next.set(k, v);
    else next.delete(k);
    if (k !== 'page') next.delete('page');
    setParams(next, { replace: true });
  };

  return (
    <div className="stack">
      <div className="toolbar">
        <label className="sr-only" htmlFor="dep-env">
          Entorno
        </label>
        <Select id="dep-env" value={environment ?? ''} onChange={(e) => set('environment', e.target.value)}>
          <option value="">Todos los entornos</option>
          {environments.data?.map((env) => (
            <option key={env} value={env}>
              {env}
            </option>
          ))}
        </Select>
        <label className="sr-only" htmlFor="dep-status">
          Estado
        </label>
        <Select id="dep-status" value={status ?? ''} onChange={(e) => set('status', e.target.value)}>
          <option value="">Todos los estados</option>
          <option value="Success">Correctos</option>
          <option value="Failure">Fallidos</option>
          <option value="InProgress">En curso</option>
        </Select>
      </div>
      <div className="card">
        {deployments.isPending ? (
          <LoadingState />
        ) : deployments.error ? (
          <ErrorState error={deployments.error} onRetry={() => void deployments.refetch()} />
        ) : deployments.data.items.length === 0 ? (
          <EmptyState title="Sin despliegues registrados" description="Los despliegues aparecerán cuando GitHub o tu CI los notifiquen." />
        ) : (
          <>
            <div className="table-wrap" style={{ opacity: deployments.isPlaceholderData ? 0.6 : 1 }}>
              <table className="table">
                <thead>
                  <tr>
                    <th scope="col">Entorno</th>
                    <th scope="col">Estado</th>
                    <th scope="col">Versión</th>
                    <th scope="col">Origen</th>
                    <th scope="col">Inicio</th>
                    <th scope="col">Duración</th>
                  </tr>
                </thead>
                <tbody>
                  {deployments.data.items.map((d) => (
                    <tr key={d.id}>
                      <td>
                        <strong>{d.environment}</strong>
                        {d.description ? <div className="muted small truncate" style={{ maxWidth: 260 }}>{d.description}</div> : null}
                      </td>
                      <td>
                        <DeploymentBadge status={d.status} />
                      </td>
                      <td className="mono small">
                        <ExternalLink href={d.url}>{d.ref ?? '—'}</ExternalLink>
                        {d.commitSha ? <div className="muted">{d.commitSha.slice(0, 7)}</div> : null}
                      </td>
                      <td className="small">
                        {d.repositoryFullName ?? 'API'}
                        {d.creator ? <div className="muted">por {d.creator}</div> : null}
                      </td>
                      <td className="small" style={{ whiteSpace: 'nowrap' }}>
                        {formatDateTime(d.createdAt)}
                      </td>
                      <td className="small">{formatDuration(d.durationSeconds)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={deployments.data.page} totalPages={deployments.data.totalPages} totalCount={deployments.data.totalCount} onPage={(p) => set('page', String(p))} />
          </>
        )}
      </div>
    </div>
  );
}

function RunsTable() {
  const { orgId } = useOrg();
  const [params, setParams] = useSearchParams();
  const failed = params.get('failed') === 'true' || undefined;
  const branch = params.get('branch') ?? undefined;
  const page = Number(params.get('page') ?? '1');
  const runs = usePipelineRuns(orgId, { failed, branch, page });
  const set = (k: string, v: string) => {
    const next = new URLSearchParams(params);
    if (v) next.set(k, v);
    else next.delete(k);
    if (k !== 'page') next.delete('page');
    setParams(next, { replace: true });
  };

  return (
    <div className="stack">
      <div className="toolbar">
        <label className="row small" style={{ gap: 6 }}>
          <input type="checkbox" checked={failed === true} onChange={(e) => set('failed', e.target.checked ? 'true' : '')} /> Solo fallidas
        </label>
      </div>
      <div className="card">
        {runs.isPending ? (
          <LoadingState />
        ) : runs.error ? (
          <ErrorState error={runs.error} onRetry={() => void runs.refetch()} />
        ) : runs.data.items.length === 0 ? (
          <EmptyState title="Sin ejecuciones" description="Las ejecuciones de GitHub Actions (workflow_run) o de tu CI aparecerán aquí." />
        ) : (
          <>
            <div className="table-wrap" style={{ opacity: runs.isPlaceholderData ? 0.6 : 1 }}>
              <table className="table">
                <thead>
                  <tr>
                    <th scope="col">Pipeline</th>
                    <th scope="col">Resultado</th>
                    <th scope="col">Rama</th>
                    <th scope="col">Commit</th>
                    <th scope="col">Disparado por</th>
                    <th scope="col">Inicio</th>
                    <th scope="col">Duración</th>
                  </tr>
                </thead>
                <tbody>
                  {runs.data.items.map((r) => (
                    <tr key={r.id}>
                      <td>
                        <ExternalLink href={r.htmlUrl}>
                          {r.pipelineName}
                          {r.runNumber ? ` #${r.runNumber}` : ''}
                        </ExternalLink>
                        {r.attempt > 1 ? <Badge>intento {r.attempt}</Badge> : null}
                        <div className="muted small">{r.repositoryFullName ?? 'API'}</div>
                      </td>
                      <td>
                        <RunBadge status={r.status} conclusion={r.conclusion} />
                      </td>
                      <td className="mono small">
                        <button className="btn btn-ghost btn-sm mono" onClick={() => set('branch', r.branch ?? '')} title="Filtrar por esta rama">
                          {r.branch ?? '—'}
                        </button>
                      </td>
                      <td className="mono small">{r.commitSha?.slice(0, 7) ?? '—'}</td>
                      <td className="small">
                        {r.actor ?? '—'}
                        {r.trigger ? <div className="muted">{r.trigger}</div> : null}
                      </td>
                      <td className="small" style={{ whiteSpace: 'nowrap' }}>
                        {formatRelative(r.createdAt)}
                      </td>
                      <td className="small">{formatDuration(r.durationSeconds)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {branch ? (
              <p className="small" style={{ padding: '8px 16px' }}>
                Filtrando rama <span className="mono">{branch}</span> ·{' '}
                <button className="btn btn-ghost btn-sm" onClick={() => set('branch', '')}>
                  Quitar filtro
                </button>
              </p>
            ) : null}
            <Pagination page={runs.data.page} totalPages={runs.data.totalPages} totalCount={runs.data.totalCount} onPage={(p) => set('page', String(p))} />
          </>
        )}
      </div>
    </div>
  );
}

function CodeEvents() {
  const { orgId } = useOrg();
  const [params, setParams] = useSearchParams();
  const page = Number(params.get('page') ?? '1');
  const events = useCodeEvents(orgId, page);
  return (
    <div className="card">
      {events.isPending ? (
        <LoadingState />
      ) : events.error ? (
        <ErrorState error={events.error} />
      ) : events.data.items.length === 0 ? (
        <EmptyState title="Sin actividad de código" description="Los pushes y pull requests de repositorios autorizados aparecerán aquí." />
      ) : (
        <>
          <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
            {events.data.items.map((e) => (
              <li key={e.id} className="row-between" style={{ padding: '10px 20px', borderBottom: '1px solid var(--border)' }}>
                <div className="row" style={{ gap: 10, minWidth: 0 }}>
                  {e.kind === 'PullRequest' ? <GitPullRequest size={16} aria-label="Pull request" /> : <GitCommitHorizontal size={16} aria-label="Push" />}
                  <div style={{ minWidth: 0 }}>
                    <ExternalLink href={e.url}>{e.title}</ExternalLink>
                    <div className="muted small truncate">
                      {e.repositoryFullName} · {e.branch ?? '—'} · {e.author ?? 'desconocido'}
                      {e.kind === 'Push' && e.commitCount ? ` · ${e.commitCount} commit(s)` : ''}
                    </div>
                  </div>
                </div>
                <div className="row" style={{ gap: 8 }}>
                  {e.state ? <Badge tone={e.state === 'merged' ? 'accent' : e.state === 'open' ? 'info' : undefined}>{e.state}</Badge> : null}
                  <span className="muted small">{formatRelative(e.occurredAt)}</span>
                </div>
              </li>
            ))}
          </ul>
          <Pagination page={events.data.page} totalPages={events.data.totalPages} totalCount={events.data.totalCount} onPage={(p) => setParams({ tab: 'code', page: String(p) })} />
        </>
      )}
    </div>
  );
}
