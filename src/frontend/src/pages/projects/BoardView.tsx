import { useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import {
  DndContext,
  DragOverlay,
  KeyboardSensor,
  PointerSensor,
  useDraggable,
  useDroppable,
  useSensor,
  useSensors,
  type DragEndEvent,
  type DragStartEvent,
} from '@dnd-kit/core';
import { CalendarClock, Plus } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useBoard, useTransitionWorkItem } from '../../api/hooks';
import type { BoardColumn, Priority, WorkItemSummary, WorkItemType } from '../../api/types';
import { useOrg, useToast } from '../../app/providers';
import { SearchInput } from '../../components/SearchInput';
import { Avatar, Button, ErrorState, LoadingState, Select } from '../../components/ui';
import { formatDate, isOverdue, PRIORITIES, priorityLabel, typeLabel } from '../../lib/format';
import { useProjectDetail } from './ProjectLayout';
import { CreateWorkItemDialog, PriorityTag, TypeIcon } from './workItemWidgets';

function CardContent({ item }: { item: WorkItemSummary }) {
  const overdue = isOverdue(item.dueDate, item.statusCategory);
  return (
    <>
      <div className="row" style={{ gap: 6 }}>
        <TypeIcon type={item.type} />
        <span className="mono muted small">{item.key}</span>
      </div>
      <span className="kcard-title">{item.title}</span>
      {item.labels.length > 0 ? (
        <div className="row" style={{ gap: 4 }}>
          {item.labels.map((l) => (
            <span key={l.id} className="badge" style={{ height: 20, fontSize: 11 }}>
              <span className="dot" style={{ background: l.color }} aria-hidden />
              {l.name}
            </span>
          ))}
        </div>
      ) : null}
      <div className="kcard-meta">
        <PriorityTag priority={item.priority} />
        {item.dueDate ? (
          <span className="row" style={{ gap: 3, color: overdue ? 'var(--danger)' : undefined }} title={overdue ? 'Vencida' : 'Fecha límite'}>
            <CalendarClock size={13} aria-hidden />
            {formatDate(item.dueDate)}
            {overdue ? <span className="sr-only">(vencida)</span> : null}
          </span>
        ) : null}
        {item.subtaskCount > 0 ? (
          <span title="Subtareas completadas">
            {item.subtaskCount - item.openSubtaskCount}/{item.subtaskCount}
          </span>
        ) : null}
        <span style={{ marginLeft: 'auto' }}>{item.assignee ? <Avatar name={item.assignee.displayName} small /> : null}</span>
      </div>
    </>
  );
}

function DraggableCard({ item, canDrag, href }: { item: WorkItemSummary; canDrag: boolean; href: string }) {
  const { attributes, listeners, setNodeRef, isDragging } = useDraggable({ id: item.id, data: { item }, disabled: !canDrag });
  return (
    <div ref={setNodeRef} className={`kcard ${isDragging ? 'dragging' : ''}`} {...attributes} {...listeners} aria-roledescription="tarjeta arrastrable">
      <Link to={href} style={{ color: 'inherit', textDecoration: 'none' }} className="stack-sm" aria-label={`${item.key}: ${item.title}`}>
        <CardContent item={item} />
      </Link>
    </div>
  );
}

function Column({
  column,
  state,
  children,
  onAdd,
}: {
  column: BoardColumn;
  state: 'idle' | 'allowed' | 'blocked';
  children: React.ReactNode;
  onAdd?: () => void;
}) {
  const { setNodeRef, isOver } = useDroppable({ id: column.status.id, disabled: state === 'blocked' });
  const shown = column.items.length;
  return (
    <section
      ref={setNodeRef}
      className={`column ${isOver && state === 'allowed' ? 'drop-target' : ''} ${state === 'blocked' ? 'drop-blocked' : ''}`}
      aria-label={`${column.status.name}, ${column.totalCount} tareas`}
    >
      <header className="column-header">
        <span className="truncate">{column.status.name}</span>
        <span className="badge">{column.totalCount}</span>
        {onAdd ? (
          <Button variant="ghost" size="sm" icon onClick={onAdd} aria-label={`Añadir tarea en ${column.status.name}`} style={{ marginLeft: 'auto' }}>
            <Plus size={15} />
          </Button>
        ) : null}
      </header>
      <div className="column-body">
        {children}
        {column.totalCount > shown ? <p className="muted small" style={{ padding: 4 }}>Mostrando {shown} de {column.totalCount}. Usa la vista de lista para ver todas.</p> : null}
        {column.status.category === 'Done' || column.status.category === 'Cancelled' ? (
          <p className="muted small" style={{ padding: 4 }}>Se muestran las cerradas en los últimos 14 días.</p>
        ) : null}
      </div>
    </section>
  );
}

export function BoardView() {
  const project = useProjectDetail();
  const { orgId, path } = useOrg();
  const toast = useToast();
  const [params, setParams] = useSearchParams();
  const filters = {
    q: params.get('q') || undefined,
    assignee: params.get('assignee') || undefined,
    type: (params.get('type') as WorkItemType | null) ?? undefined,
    priority: (params.get('priority') as Priority | null) ?? undefined,
  };
  const board = useBoard(orgId, project.id, filters);
  const transition = useTransitionWorkItem(orgId, project.id);
  const [active, setActive] = useState<WorkItemSummary | null>(null);
  const [createIn, setCreateIn] = useState<string | null>(null);
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 6 } }), useSensor(KeyboardSensor));

  const allowedTargets = useMemo(() => {
    if (!active || !board.data) return new Set<string>();
    return new Set(board.data.transitions.filter((t) => t.fromStatusId === active.statusId).map((t) => t.toStatusId));
  }, [active, board.data]);

  const setFilter = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
  };

  if (board.isPending) return <LoadingState label="Cargando tablero…" />;
  if (board.error) return <ErrorState error={board.error} onRetry={() => void board.refetch()} />;

  const canEdit = board.data.permissions.canEditItems;

  const onDragStart = (e: DragStartEvent) => setActive((e.active.data.current as { item: WorkItemSummary }).item);
  const onDragEnd = (e: DragEndEvent) => {
    const item = (e.active.data.current as { item: WorkItemSummary }).item;
    setActive(null);
    const target = e.over?.id as string | undefined;
    if (!target || target === item.statusId) return;
    if (!board.data.transitions.some((t) => t.fromStatusId === item.statusId && t.toStatusId === target)) {
      toast('Ese cambio de estado no está permitido por el flujo del proyecto.', 'error');
      return;
    }
    transition.mutate(
      { number: item.number, statusId: target },
      { onError: (error) => toast(errorMessage(error), 'error') },
    );
  };

  return (
    <div className="stack">
      <div className="toolbar" role="search">
        <SearchInput label="Buscar tareas" placeholder="Buscar…" value={filters.q ?? ''} onChange={(v) => setFilter('q', v)} />
        <label className="sr-only" htmlFor="board-assignee">
          Responsable
        </label>
        <Select id="board-assignee" value={filters.assignee ?? ''} onChange={(e) => setFilter('assignee', e.target.value)}>
          <option value="">Todos los responsables</option>
          <option value="me">Asignadas a mí</option>
          <option value="none">Sin asignar</option>
          {project.members.map((m) => (
            <option key={m.userId} value={m.userId}>
              {m.displayName}
            </option>
          ))}
        </Select>
        <label className="sr-only" htmlFor="board-type">
          Tipo
        </label>
        <Select id="board-type" value={filters.type ?? ''} onChange={(e) => setFilter('type', e.target.value)}>
          <option value="">Todos los tipos</option>
          {(['Task', 'Issue', 'Subtask'] as const).map((t) => (
            <option key={t} value={t}>
              {typeLabel[t]}
            </option>
          ))}
        </Select>
        <label className="sr-only" htmlFor="board-priority">
          Prioridad
        </label>
        <Select id="board-priority" value={filters.priority ?? ''} onChange={(e) => setFilter('priority', e.target.value)}>
          <option value="">Todas las prioridades</option>
          {PRIORITIES.map((p) => (
            <option key={p} value={p}>
              {priorityLabel[p]}
            </option>
          ))}
        </Select>
        {canEdit ? (
          <Button variant="primary" onClick={() => setCreateIn('')} style={{ marginLeft: 'auto' }}>
            <Plus size={16} aria-hidden /> Nueva tarea
          </Button>
        ) : (
          <span className="muted small" style={{ marginLeft: 'auto' }}>
            {project.status === 'Archived' ? 'Proyecto archivado (solo lectura).' : 'Solo lectura: debes ser miembro del proyecto para editar.'}
          </span>
        )}
      </div>

      <DndContext sensors={sensors} onDragStart={onDragStart} onDragEnd={onDragEnd} onDragCancel={() => setActive(null)}>
        <div className="board" style={{ opacity: board.isPlaceholderData || transition.isPending ? 0.7 : 1 }}>
          {board.data.columns.map((column) => (
            <Column
              key={column.status.id}
              column={column}
              state={!active ? 'idle' : column.status.id === active.statusId || allowedTargets.has(column.status.id) ? 'allowed' : 'blocked'}
              onAdd={canEdit && (column.status.category === 'Todo' || column.status.category === 'InProgress') ? () => setCreateIn(column.status.id) : undefined}
            >
              {column.items.map((item) => (
                <DraggableCard key={item.id} item={item} canDrag={canEdit} href={path(`/projects/${project.id}/items/${item.number}`)} />
              ))}
            </Column>
          ))}
        </div>
        <DragOverlay>{active ? <div className="kcard" style={{ boxShadow: 'var(--shadow-lg)' }}><CardContent item={active} /></div> : null}</DragOverlay>
      </DndContext>

      <CreateWorkItemDialog open={createIn !== null} onClose={() => setCreateIn(null)} project={project} defaultStatusId={createIn || undefined} />
    </div>
  );
}
