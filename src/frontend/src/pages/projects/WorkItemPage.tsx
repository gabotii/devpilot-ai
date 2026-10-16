import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { ChevronLeft, Pencil, Plus, Trash2 } from 'lucide-react';
import { ApiError, errorMessage } from '../../api/client';
import {
  useCommentMutations,
  useComments,
  useDeleteWorkItem,
  useHistory,
  useTransitionWorkItem,
  useUpdateWorkItem,
  useWorkItem,
} from '../../api/hooks';
import type { Priority, WorkItemDetail } from '../../api/types';
import { useOrg, useToast } from '../../app/providers';
import { Avatar, Button, Card, ConfirmDialog, ErrorState, Field, InlineError, Input, LoadingState, Select, Textarea } from '../../components/ui';
import { formatDate, formatDateTime, formatRelative, isOverdue, PRIORITIES, priorityLabel, typeLabel } from '../../lib/format';
import { useProjectDetail } from './ProjectLayout';
import { CreateWorkItemDialog, PriorityTag, StatusBadge, TypeIcon } from './workItemWidgets';

const FIELD_LABELS: Record<string, string> = {
  created: 'creó la tarea',
  title: 'cambió el título',
  description: 'editó la descripción',
  priority: 'cambió la prioridad',
  assignee: 'cambió el responsable',
  dueDate: 'cambió la fecha límite',
  labels: 'cambió las etiquetas',
  status: 'cambió el estado',
};

interface EditState {
  title: string;
  description: string;
  priority: Priority;
  assigneeId: string;
  dueDate: string;
  labelIds: string[];
}

function toEdit(d: WorkItemDetail): EditState {
  return {
    title: d.item.title,
    description: d.description ?? '',
    priority: d.item.priority,
    assigneeId: d.item.assignee?.id ?? '',
    dueDate: d.item.dueDate ?? '',
    labelIds: d.item.labels.map((l) => l.id),
  };
}

export function WorkItemPage() {
  const { number = '' } = useParams();
  // Keyed by item number so edit state never leaks from one item to another.
  return <WorkItemView key={number} number={Number(number)} />;
}

function WorkItemView({ number }: { number: number }) {
  const project = useProjectDetail();
  const { orgId, path } = useOrg();
  const navigate = useNavigate();
  const toast = useToast();
  const detail = useWorkItem(orgId, project.id, number);
  const update = useUpdateWorkItem(orgId, project.id, number);
  const transition = useTransitionWorkItem(orgId, project.id);
  const remove = useDeleteWorkItem(orgId, project.id);
  const [edit, setEdit] = useState<EditState | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [addingSubtask, setAddingSubtask] = useState(false);
  const [tab, setTab] = useState<'comments' | 'history'>('comments');

  if (!Number.isInteger(number) || number < 1) return <ErrorState error={new ApiError(404, 'No encontrado')} />;
  if (detail.isPending) return <LoadingState />;
  if (detail.error) return <ErrorState error={detail.error} onRetry={() => void detail.refetch()} />;

  const d = detail.data;
  const item = d.item;
  const canEdit = d.permissions.canEditItems;
  const staleConflict = update.error instanceof ApiError && update.error.status === 409;

  const save = () => {
    if (!edit) return;
    update.mutate(
      {
        title: edit.title,
        description: edit.description || null,
        priority: edit.priority,
        assigneeId: edit.assigneeId || null,
        dueDate: edit.dueDate || null,
        labelIds: edit.labelIds,
        version: d.version,
      },
      {
        onSuccess: () => {
          setEdit(null);
          toast('Cambios guardados');
        },
      },
    );
  };

  const move = (statusId: string) =>
    transition.mutate(
      { number, statusId },
      { onSuccess: () => toast('Estado actualizado'), onError: (e) => toast(errorMessage(e), 'error') },
    );

  return (
    <div className="stack">
      <div className="row-between">
        <Link to={item.parentId && d.parent ? path(`/projects/${project.id}/items/${d.parent.number}`) : path(`/projects/${project.id}`)} className="muted small row" style={{ gap: 4 }}>
          <ChevronLeft size={14} aria-hidden /> {d.parent ? `Volver a ${d.parent.key}` : 'Volver al tablero'}
        </Link>
        {canEdit && !edit ? (
          <div className="row">
            <Button onClick={() => setEdit(toEdit(d))}>
              <Pencil size={15} aria-hidden /> Editar
            </Button>
            <Button variant="ghost" onClick={() => setConfirmDelete(true)} aria-label="Eliminar tarea">
              <Trash2 size={15} aria-hidden />
            </Button>
          </div>
        ) : null}
      </div>

      <div className="detail-layout">
        <div className="stack">
          <Card>
            <div className="stack">
              <div className="row" style={{ gap: 8 }}>
                <TypeIcon type={item.type} />
                <span className="mono muted">{item.key}</span>
                <StatusBadge name={item.statusName} category={item.statusCategory} />
                {d.parent ? (
                  <span className="muted small">
                    Subtarea de <Link to={path(`/projects/${project.id}/items/${d.parent.number}`)}>{d.parent.key}</Link>
                  </span>
                ) : null}
              </div>
              {edit ? (
                <>
                  <Field label="Título">{(p) => <Input {...p} value={edit.title} maxLength={200} onChange={(e) => setEdit({ ...edit, title: e.target.value })} />}</Field>
                  <Field label="Descripción">
                    {(p) => <Textarea {...p} rows={8} value={edit.description} onChange={(e) => setEdit({ ...edit, description: e.target.value })} />}
                  </Field>
                </>
              ) : (
                <>
                  <h1 style={{ fontSize: 20 }}>{item.title}</h1>
                  {d.description ? <div className="prose">{d.description}</div> : <p className="muted">Sin descripción.</p>}
                </>
              )}
              {edit ? (
                <div className="stack-sm">
                  {staleConflict ? (
                    <div className="alert alert-warning" role="alert">
                      Otra persona modificó esta tarea mientras editabas.{' '}
                      <Button size="sm" onClick={() => void detail.refetch().then((r) => r.data && setEdit(toEdit(r.data)))}>
                        Recargar sus cambios
                      </Button>
                    </div>
                  ) : (
                    <InlineError error={update.error} />
                  )}
                  <div className="row">
                    <Button variant="primary" loading={update.isPending} onClick={save} disabled={!edit.title.trim()}>
                      Guardar
                    </Button>
                    <Button onClick={() => { setEdit(null); update.reset(); }}>Cancelar</Button>
                  </div>
                </div>
              ) : null}
            </div>
          </Card>

          {item.type !== 'Subtask' ? (
            <Card
              title={`Subtareas (${item.subtaskCount - item.openSubtaskCount}/${item.subtaskCount})`}
              actions={
                canEdit && item.statusCategory !== 'Done' && item.statusCategory !== 'Cancelled' ? (
                  <Button size="sm" onClick={() => setAddingSubtask(true)}>
                    <Plus size={14} aria-hidden /> Añadir
                  </Button>
                ) : undefined
              }
              bodyClassName=""
            >
              {d.subtasks.length === 0 ? (
                <p className="muted card-body">Sin subtareas.</p>
              ) : (
                <ul className="stack-sm" style={{ listStyle: 'none', margin: 0, padding: '12px 20px' }}>
                  {d.subtasks.map((s) => (
                    <li key={s.id} className="row-between">
                      <Link to={path(`/projects/${project.id}/items/${s.number}`)} className="row" style={{ gap: 8 }}>
                        <span className="mono small muted">{s.key}</span>
                        <span style={{ textDecoration: s.statusCategory === 'Done' ? 'line-through' : undefined }}>{s.title}</span>
                      </Link>
                      <StatusBadge name={s.statusName} category={s.statusCategory} />
                    </li>
                  ))}
                </ul>
              )}
            </Card>
          ) : null}

          <section className="card">
            <div className="tabs" role="tablist" style={{ padding: '0 16px' }}>
              <button role="tab" aria-selected={tab === 'comments'} className={`tab btn-ghost ${tab === 'comments' ? 'active' : ''}`} style={{ background: 'none', border: 0, cursor: 'pointer', font: 'inherit' }} onClick={() => setTab('comments')}>
                Comentarios ({d.commentCount})
              </button>
              <button role="tab" aria-selected={tab === 'history'} className={`tab ${tab === 'history' ? 'active' : ''}`} style={{ background: 'none', border: 0, cursor: 'pointer', font: 'inherit' }} onClick={() => setTab('history')}>
                Historial
              </button>
            </div>
            <div className="card-body" role="tabpanel">
              {tab === 'comments' ? <Comments number={number} canComment={project.status === 'Active'} /> : <History number={number} />}
            </div>
          </section>
        </div>

        <aside className="card card-body stack" aria-label="Propiedades">
          <div className="field">
            <label htmlFor="status-select" className="field-label">
              Estado
            </label>
            {canEdit && d.allowedTransitions.length > 0 ? (
              <Select id="status-select" value={item.statusId} disabled={transition.isPending} onChange={(e) => move(e.target.value)}>
                <option value={item.statusId}>{item.statusName}</option>
                {d.allowedTransitions.map((s) => (
                  <option key={s.id} value={s.id}>
                    → {s.name}
                  </option>
                ))}
              </Select>
            ) : (
              <StatusBadge name={item.statusName} category={item.statusCategory} />
            )}
            {item.openSubtaskCount > 0 ? <span className="field-hint">No se puede completar con {item.openSubtaskCount} subtarea(s) abierta(s).</span> : null}
          </div>
          <dl className="props">
            <dt>Tipo</dt>
            <dd>{typeLabel[item.type]}</dd>
            <dt>Prioridad</dt>
            <dd>
              {edit ? (
                <Select aria-label="Prioridad" value={edit.priority} onChange={(e) => setEdit({ ...edit, priority: e.target.value as Priority })}>
                  {PRIORITIES.map((p) => (
                    <option key={p} value={p}>
                      {priorityLabel[p]}
                    </option>
                  ))}
                </Select>
              ) : (
                <PriorityTag priority={item.priority} />
              )}
            </dd>
            <dt>Responsable</dt>
            <dd>
              {edit ? (
                <Select aria-label="Responsable" value={edit.assigneeId} onChange={(e) => setEdit({ ...edit, assigneeId: e.target.value })}>
                  <option value="">Sin asignar</option>
                  {project.members.map((m) => (
                    <option key={m.userId} value={m.userId}>
                      {m.displayName}
                    </option>
                  ))}
                </Select>
              ) : item.assignee ? (
                <span className="row" style={{ gap: 6 }}>
                  <Avatar name={item.assignee.displayName} small /> {item.assignee.displayName}
                </span>
              ) : (
                <span className="muted">Sin asignar</span>
              )}
            </dd>
            <dt>Fecha límite</dt>
            <dd>
              {edit ? (
                <Input aria-label="Fecha límite" type="date" value={edit.dueDate} onChange={(e) => setEdit({ ...edit, dueDate: e.target.value })} />
              ) : (
                <span style={{ color: isOverdue(item.dueDate, item.statusCategory) ? 'var(--danger)' : undefined }}>
                  {formatDate(item.dueDate)}
                  {isOverdue(item.dueDate, item.statusCategory) ? ' (vencida)' : ''}
                </span>
              )}
            </dd>
            <dt>Etiquetas</dt>
            <dd>
              {edit ? (
                <div className="row" style={{ gap: 4 }}>
                  {project.labels.length === 0 ? <span className="muted small">Sin etiquetas en el proyecto</span> : null}
                  {project.labels.map((l) => (
                    <label key={l.id} className="badge" style={{ cursor: 'pointer' }}>
                      <input
                        type="checkbox"
                        checked={edit.labelIds.includes(l.id)}
                        onChange={(e) =>
                          setEdit({ ...edit, labelIds: e.target.checked ? [...edit.labelIds, l.id] : edit.labelIds.filter((x) => x !== l.id) })
                        }
                      />
                      {l.name}
                    </label>
                  ))}
                </div>
              ) : item.labels.length ? (
                <div className="row" style={{ gap: 4 }}>
                  {item.labels.map((l) => (
                    <span key={l.id} className="badge">
                      <span className="dot" style={{ background: l.color }} aria-hidden />
                      {l.name}
                    </span>
                  ))}
                </div>
              ) : (
                <span className="muted">—</span>
              )}
            </dd>
            <dt>Informador</dt>
            <dd>{d.reporter?.displayName ?? '—'}</dd>
            <dt>Creada</dt>
            <dd className="small">{formatDateTime(item.createdAt)}</dd>
            <dt>Actualizada</dt>
            <dd className="small">{formatRelative(item.updatedAt)}</dd>
            {item.completedAt ? (
              <>
                <dt>Completada</dt>
                <dd className="small">{formatDateTime(item.completedAt)}</dd>
              </>
            ) : null}
          </dl>
        </aside>
      </div>

      <ConfirmDialog
        open={confirmDelete}
        title={`Eliminar ${item.key}`}
        destructive
        confirmLabel="Eliminar definitivamente"
        message={
          <p>
            Se eliminará <strong>{item.title}</strong>
            {item.subtaskCount > 0 ? `, sus ${item.subtaskCount} subtarea(s)` : ''}, los comentarios y el historial. Esta acción no se puede deshacer.
          </p>
        }
        loading={remove.isPending}
        error={remove.error}
        onCancel={() => setConfirmDelete(false)}
        onConfirm={() =>
          remove.mutate(number, {
            onSuccess: () => {
              toast(`${item.key} eliminada`);
              navigate(path(`/projects/${project.id}`));
            },
          })
        }
      />
      <CreateWorkItemDialog open={addingSubtask} onClose={() => setAddingSubtask(false)} project={project} parent={{ id: item.id, key: item.key }} />
    </div>
  );
}

function Comments({ number, canComment }: { number: number; canComment: boolean }) {
  const project = useProjectDetail();
  const { orgId, hasRole } = useOrg();
  const comments = useComments(orgId, project.id, number);
  const mutations = useCommentMutations(orgId, project.id, number);
  const [draft, setDraft] = useState('');
  const [editing, setEditing] = useState<{ id: string; body: string } | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);

  if (comments.isPending) return <LoadingState />;
  if (comments.error) return <ErrorState error={comments.error} />;

  return (
    <div className="stack">
      {comments.data.items.length === 0 ? <p className="muted">Todavía no hay comentarios.</p> : null}
      {comments.data.items.map((c) => (
        <article key={c.id} className="comment stack-sm">
          <div className="row-between">
            <span className="row" style={{ gap: 8 }}>
              <Avatar name={c.author?.displayName ?? '?'} small />
              <strong>{c.author?.displayName ?? 'Usuario eliminado'}</strong>
              <span className="muted small">
                {formatRelative(c.createdAt)}
                {c.editedAt ? ' · editado' : ''}
              </span>
            </span>
            <span className="row" style={{ gap: 2 }}>
              {c.canEdit ? (
                <Button size="sm" variant="ghost" onClick={() => setEditing({ id: c.id, body: c.body })}>
                  Editar
                </Button>
              ) : null}
              {c.canDelete ? (
                <Button size="sm" variant="ghost" onClick={() => setDeleting(c.id)}>
                  Eliminar
                </Button>
              ) : null}
            </span>
          </div>
          {editing?.id === c.id ? (
            <div className="stack-sm">
              <Textarea aria-label="Editar comentario" value={editing.body} onChange={(e) => setEditing({ ...editing, body: e.target.value })} />
              <div className="row">
                <Button size="sm" variant="primary" loading={mutations.edit.isPending} disabled={!editing.body.trim()} onClick={() => mutations.edit.mutate(editing, { onSuccess: () => setEditing(null) })}>
                  Guardar
                </Button>
                <Button size="sm" onClick={() => setEditing(null)}>
                  Cancelar
                </Button>
              </div>
            </div>
          ) : (
            <div className="prose">{c.body}</div>
          )}
        </article>
      ))}
      {canComment && hasRole('Developer') ? (
        <form
          className="stack-sm"
          onSubmit={(e) => {
            e.preventDefault();
            if (!draft.trim()) return;
            mutations.add.mutate(draft, { onSuccess: () => setDraft('') });
          }}
        >
          <label htmlFor="new-comment" className="field-label">
            Añadir comentario
          </label>
          <Textarea id="new-comment" value={draft} maxLength={10000} onChange={(e) => setDraft(e.target.value)} placeholder="Escribe un comentario…" />
          <InlineError error={mutations.add.error} />
          <div>
            <Button type="submit" variant="primary" size="sm" loading={mutations.add.isPending} disabled={!draft.trim()}>
              Comentar
            </Button>
          </div>
        </form>
      ) : null}
      <ConfirmDialog
        open={deleting !== null}
        title="Eliminar comentario"
        message="El comentario se eliminará definitivamente."
        confirmLabel="Eliminar"
        destructive
        loading={mutations.remove.isPending}
        error={mutations.remove.error}
        onCancel={() => setDeleting(null)}
        onConfirm={() => deleting && mutations.remove.mutate(deleting, { onSuccess: () => setDeleting(null) })}
      />
    </div>
  );
}

function History({ number }: { number: number }) {
  const project = useProjectDetail();
  const { orgId } = useOrg();
  const history = useHistory(orgId, project.id, number);
  if (history.isPending) return <LoadingState />;
  if (history.error) return <ErrorState error={history.error} />;
  return (
    <ul className="timeline">
      {history.data.map((h) => (
        <li key={h.id}>
          <Avatar name={h.actor?.displayName ?? '?'} small />
          <div>
            <div>
              <strong>{h.actor?.displayName ?? 'Sistema'}</strong> {FIELD_LABELS[h.field] ?? `cambió ${h.field}`}
              {h.field !== 'created' && h.field !== 'description' ? (
                <span className="muted">
                  {' '}
                  {h.oldValue || '—'} → {h.newValue || '—'}
                </span>
              ) : null}
            </div>
            <time className="muted small" dateTime={h.at}>
              {formatDateTime(h.at)}
            </time>
          </div>
        </li>
      ))}
    </ul>
  );
}
