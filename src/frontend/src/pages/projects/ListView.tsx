import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { ArrowDown, ArrowUp, Plus } from 'lucide-react';
import { useWorkItems } from '../../api/hooks';
import type { Priority, StatusCategory, WorkItemFilters, WorkItemSummary, WorkItemType } from '../../api/types';
import { useOrg } from '../../app/providers';
import { SearchInput } from '../../components/SearchInput';
import { Avatar, Button, EmptyState, ErrorState, LoadingState, Pagination, Select } from '../../components/ui';
import { categoryLabel, formatDate, formatRelative, isOverdue, PRIORITIES, priorityLabel, STATUS_CATEGORIES, typeLabel } from '../../lib/format';
import { StatusBadge, PriorityTag, TypeIcon, CreateWorkItemDialog } from './workItemWidgets';
import type { ProjectDetail } from '../../api/types';

const COL_KEY = { field: 'number', label: 'Clave' };
const COL_TITLE = { field: 'title', label: 'Título' };
const COL_PRIORITY = { field: 'priority', label: 'Prioridad' };
const COL_DUE = { field: 'dueDate', label: 'Vence' };
const COL_UPDATED = { field: 'updatedAt', label: 'Actualizada' };

/** Reusable filtered/sorted/paginated work item table. Used per project and org-wide (incident center). */
export function WorkItemTable({
  projectId,
  fixed,
  project,
  emptyTitle = 'No hay tareas que coincidan',
}: {
  projectId?: string;
  fixed?: Partial<WorkItemFilters>;
  project?: ProjectDetail;
  emptyTitle?: string;
}) {
  const { orgId, path } = useOrg();
  const [params, setParams] = useSearchParams();
  const [creating, setCreating] = useState(false);
  const sort = params.get('sort') ?? '-updatedAt';
  const filters: WorkItemFilters = {
    q: params.get('q') || undefined,
    category: (params.get('category') as StatusCategory | null) ?? undefined,
    open: params.get('category') ? undefined : params.get('open') === 'all' ? undefined : true,
    assignee: params.get('assignee') || undefined,
    priority: (params.get('priority') as Priority | null) ?? undefined,
    type: (params.get('type') as WorkItemType | null) ?? undefined,
    overdue: params.get('overdue') === 'true' || undefined,
    sort,
    page: Number(params.get('page') ?? '1'),
    pageSize: 25,
    ...fixed,
  };
  const items = useWorkItems(orgId, projectId, filters);

  const set = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    if (key !== 'page') next.delete('page');
    setParams(next, { replace: true });
  };

  const toggleSort = (field: string) => set('sort', sort === field ? `-${field}` : field);
  const itemHref = (i: WorkItemSummary) => path(`/projects/${i.projectId}/items/${i.number}`);

  return (
    <div className="stack">
      <div className="toolbar" role="search">
        <SearchInput label="Buscar" placeholder="Título, descripción o clave (API-12)…" value={filters.q ?? ''} onChange={(v) => set('q', v)} />
        <label className="sr-only" htmlFor="list-state">
          Estado
        </label>
        <Select id="list-state" value={params.get('category') ?? params.get('open') ?? ''} onChange={(e) => {
          const v = e.target.value;
          const next = new URLSearchParams(params);
          next.delete('category');
          next.delete('open');
          next.delete('page');
          if (v === 'all') next.set('open', 'all');
          else if (v) next.set('category', v);
          setParams(next, { replace: true });
        }}>
          <option value="">Abiertas</option>
          <option value="all">Todas</option>
          {STATUS_CATEGORIES.map((c) => (
            <option key={c} value={c}>
              {categoryLabel[c]}
            </option>
          ))}
        </Select>
        {!fixed?.type ? (
          <>
            <label className="sr-only" htmlFor="list-type">
              Tipo
            </label>
            <Select id="list-type" value={filters.type ?? ''} onChange={(e) => set('type', e.target.value)}>
              <option value="">Todos los tipos</option>
              {(['Task', 'Issue', 'Subtask'] as const).map((t) => (
                <option key={t} value={t}>
                  {typeLabel[t]}
                </option>
              ))}
            </Select>
          </>
        ) : null}
        <label className="sr-only" htmlFor="list-priority">
          Prioridad
        </label>
        <Select id="list-priority" value={filters.priority ?? ''} onChange={(e) => set('priority', e.target.value)}>
          <option value="">Todas las prioridades</option>
          {PRIORITIES.map((p) => (
            <option key={p} value={p}>
              {priorityLabel[p]}
            </option>
          ))}
        </Select>
        <label className="sr-only" htmlFor="list-assignee">
          Responsable
        </label>
        <Select id="list-assignee" value={filters.assignee ?? ''} onChange={(e) => set('assignee', e.target.value)}>
          <option value="">Cualquier responsable</option>
          <option value="me">Asignadas a mí</option>
          <option value="none">Sin asignar</option>
          {project?.members.map((m) => (
            <option key={m.userId} value={m.userId}>
              {m.displayName}
            </option>
          ))}
        </Select>
        <label className="row small" style={{ gap: 6 }}>
          <input type="checkbox" checked={filters.overdue === true} onChange={(e) => set('overdue', e.target.checked ? 'true' : '')} />
          Solo vencidas
        </label>
        {project?.permissions.canEditItems ? (
          <Button variant="primary" onClick={() => setCreating(true)} style={{ marginLeft: 'auto' }}>
            <Plus size={16} aria-hidden /> Nueva tarea
          </Button>
        ) : null}
      </div>

      <div className="card">
        {items.isPending ? (
          <LoadingState />
        ) : items.error ? (
          <ErrorState error={items.error} onRetry={() => void items.refetch()} />
        ) : items.data.items.length === 0 ? (
          <EmptyState title={emptyTitle} description="Prueba a cambiar los filtros." />
        ) : (
          <>
            <div className="table-wrap" style={{ opacity: items.isPlaceholderData ? 0.6 : 1 }}>
              <table className="table">
                <thead>
                  <tr>
                    <SortHeader {...COL_KEY} sort={sort} onSort={toggleSort} />
                    <SortHeader {...COL_TITLE} sort={sort} onSort={toggleSort} />
                    <th scope="col">Estado</th>
                    <SortHeader {...COL_PRIORITY} sort={sort} onSort={toggleSort} />
                    <th scope="col">Responsable</th>
                    <SortHeader {...COL_DUE} sort={sort} onSort={toggleSort} />
                    <SortHeader {...COL_UPDATED} sort={sort} onSort={toggleSort} />
                  </tr>
                </thead>
                <tbody>
                  {items.data.items.map((i) => (
                    <tr key={i.id}>
                      <td className="mono small" style={{ whiteSpace: 'nowrap' }}>
                        <span className="row" style={{ gap: 6 }}>
                          <TypeIcon type={i.type} />
                          {i.key}
                        </span>
                      </td>
                      <td style={{ minWidth: 220 }}>
                        <Link to={itemHref(i)}>{i.title}</Link>
                      </td>
                      <td>
                        <StatusBadge name={i.statusName} category={i.statusCategory} />
                      </td>
                      <td>
                        <PriorityTag priority={i.priority} />
                      </td>
                      <td>
                        {i.assignee ? (
                          <span className="row" style={{ gap: 6 }}>
                            <Avatar name={i.assignee.displayName} small />
                            <span className="truncate" style={{ maxWidth: 140 }}>
                              {i.assignee.displayName}
                            </span>
                          </span>
                        ) : (
                          <span className="muted">—</span>
                        )}
                      </td>
                      <td style={{ color: isOverdue(i.dueDate, i.statusCategory) ? 'var(--danger)' : undefined, whiteSpace: 'nowrap' }}>
                        {formatDate(i.dueDate)}
                      </td>
                      <td className="muted small" style={{ whiteSpace: 'nowrap' }}>
                        {formatRelative(i.updatedAt)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={items.data.page} totalPages={items.data.totalPages} totalCount={items.data.totalCount} onPage={(p) => set('page', String(p))} />
          </>
        )}
      </div>
      {project ? <CreateWorkItemDialog open={creating} onClose={() => setCreating(false)} project={project} /> : null}
    </div>
  );
}

function SortHeader({ field, label, sort, onSort }: { field: string; label: string; sort: string; onSort: (f: string) => void }) {
  const active = sort.replace('-', '') === field;
  const desc = sort.startsWith('-');
  return (
    <th scope="col" aria-sort={active ? (desc ? 'descending' : 'ascending') : 'none'}>
      <button className="th-sort" onClick={() => onSort(field)}>
        {label}
        {active ? desc ? <ArrowDown size={12} aria-hidden /> : <ArrowUp size={12} aria-hidden /> : null}
      </button>
    </th>
  );
}
