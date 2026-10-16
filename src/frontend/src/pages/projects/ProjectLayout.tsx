import { createContext, useContext } from 'react';
import { Link, NavLink, Outlet, useParams } from 'react-router';
import { ChevronLeft } from 'lucide-react';
import { useProject } from '../../api/hooks';
import type { ProjectDetail } from '../../api/types';
import { useOrg } from '../../app/providers';
import { Badge, ErrorState, LoadingState } from '../../components/ui';

const ProjectContext = createContext<ProjectDetail | null>(null);

export function useProjectDetail(): ProjectDetail {
  const project = useContext(ProjectContext);
  if (!project) throw new Error('useProjectDetail must be used inside ProjectLayout');
  return project;
}

export function ProjectLayout() {
  const { projectId = '' } = useParams();
  const { orgId, path } = useOrg();
  const project = useProject(orgId, projectId);

  if (project.isPending) return <LoadingState />;
  if (project.error) return <ErrorState error={project.error} onRetry={() => void project.refetch()} />;

  const p = project.data;
  const base = path(`/projects/${p.id}`);
  const tabs = [
    { to: base, label: 'Tablero', end: true },
    { to: `${base}/list`, label: 'Lista' },
    { to: `${base}/activity`, label: 'Actividad' },
    { to: `${base}/settings`, label: 'Configuración' },
  ];

  return (
    <ProjectContext.Provider value={p}>
      <div className="page" style={{ maxWidth: 'none' }}>
        <div className="stack-sm">
          <Link to={path('/projects')} className="muted small row" style={{ gap: 4 }}>
            <ChevronLeft size={14} aria-hidden /> Proyectos
          </Link>
          <div className="page-header">
            <div>
              <div className="row">
                <h1>{p.name}</h1>
                <span className="badge mono">{p.key}</span>
                {p.status === 'Archived' ? <Badge tone="warning">Archivado · solo lectura</Badge> : null}
              </div>
              <p>{p.description ?? `${p.openItems} tareas abiertas de ${p.totalItems}`}</p>
            </div>
          </div>
          <nav className="tabs" aria-label="Secciones del proyecto">
            {tabs.map((t) => (
              <NavLink key={t.to} to={t.to} end={t.end} className={({ isActive }) => `tab ${isActive ? 'active' : ''}`}>
                {t.label}
              </NavLink>
            ))}
          </nav>
        </div>
        <Outlet />
      </div>
    </ProjectContext.Provider>
  );
}
