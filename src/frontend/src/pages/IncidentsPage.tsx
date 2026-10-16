import { WorkItemTable } from './projects/ListView';

/** Incident center: open issues across all projects of the organization. */
export function IncidentsPage() {
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Incidencias</h1>
          <p>Incidencias (bugs) de todos los proyectos.</p>
        </div>
      </div>
      <WorkItemTable fixed={{ type: 'Issue' }} emptyTitle="No hay incidencias que coincidan" />
    </div>
  );
}
