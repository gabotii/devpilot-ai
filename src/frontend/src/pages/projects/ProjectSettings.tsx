import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { Trash2 } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useActivity, useLabelMutations, useMembers, useProjectAction, useProjectMemberMutations, useUpdateProject, useWorkflowMutations } from '../../api/hooks';
import type { ProjectRole, Status, StatusCategory, Transition } from '../../api/types';
import { useOrg, useToast } from '../../app/providers';
import { ActivityList } from '../../components/ActivityList';
import { Badge, Button, Card, ConfirmDialog, ErrorState, Field, InlineError, Input, LoadingState, Pagination, Select, Textarea } from '../../components/ui';
import { categoryLabel, projectRoleLabel, roleLabel, STATUS_CATEGORIES } from '../../lib/format';
import { useProjectDetail } from './ProjectLayout';

export function ProjectActivity() {
  const project = useProjectDetail();
  const { orgId } = useOrg();
  const [page, setPage] = useState(1);
  const activity = useActivity(orgId, project.id, page);
  return (
    <Card title="Actividad del proyecto">
      {activity.isPending ? (
        <LoadingState />
      ) : activity.error ? (
        <ErrorState error={activity.error} />
      ) : (
        <div className="stack">
          <ActivityList items={activity.data.items} />
          <Pagination page={activity.data.page} totalPages={activity.data.totalPages} totalCount={activity.data.totalCount} onPage={setPage} />
        </div>
      )}
    </Card>
  );
}

export function ProjectSettings() {
  const project = useProjectDetail();
  const canManage = project.permissions.canManage;
  return (
    <div className="stack">
      {!canManage ? <div className="alert alert-info">Solo los líderes del proyecto y los administradores pueden cambiar la configuración.</div> : null}
      <GeneralSection />
      <MembersSection />
      <WorkflowSection />
      <LabelsSection />
      <DangerZone />
    </div>
  );
}

function GeneralSection() {
  const project = useProjectDetail();
  const { orgId } = useOrg();
  const toast = useToast();
  const update = useUpdateProject(orgId, project.id);
  const [name, setName] = useState(project.name);
  const [description, setDescription] = useState(project.description ?? '');
  const disabled = !project.permissions.canManage || project.status === 'Archived';

  return (
    <Card title="General">
      <form
        className="stack"
        onSubmit={(e) => {
          e.preventDefault();
          update.mutate({ name, description: description || null }, { onSuccess: () => toast('Proyecto actualizado') });
        }}
      >
        <Field label="Nombre">{(p) => <Input {...p} value={name} disabled={disabled} maxLength={100} onChange={(e) => setName(e.target.value)} />}</Field>
        <Field label="Clave" hint="La clave no se puede cambiar porque forma parte de las referencias existentes.">
          {(p) => <Input {...p} value={project.key} disabled readOnly />}
        </Field>
        <Field label="Descripción">
          {(p) => <Textarea {...p} rows={3} value={description} disabled={disabled} onChange={(e) => setDescription(e.target.value)} />}
        </Field>
        <InlineError error={update.error} />
        {!disabled ? (
          <div>
            <Button type="submit" variant="primary" loading={update.isPending} disabled={name.trim().length < 2}>
              Guardar
            </Button>
          </div>
        ) : null}
      </form>
    </Card>
  );
}

function MembersSection() {
  const project = useProjectDetail();
  const { orgId } = useOrg();
  const orgMembers = useMembers(orgId);
  const mutations = useProjectMemberMutations(orgId, project.id);
  const [userId, setUserId] = useState('');
  const [role, setRole] = useState<ProjectRole>('Member');
  const canManage = project.permissions.canManage;
  const candidates = (orgMembers.data?.items ?? []).filter((m) => !project.members.some((pm) => pm.userId === m.userId));
  const error = mutations.add.error ?? mutations.change.error ?? mutations.remove.error;

  return (
    <Card title="Miembros" description="Los miembros pueden ser asignados y, con rol Desarrollador o superior, editar tareas.">
      <div className="stack">
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Nombre</th>
                <th scope="col">Rol en la organización</th>
                <th scope="col">Rol en el proyecto</th>
                {canManage ? <th scope="col"><span className="sr-only">Acciones</span></th> : null}
              </tr>
            </thead>
            <tbody>
              {project.members.map((m) => (
                <tr key={m.userId}>
                  <td>
                    <div>{m.displayName}</div>
                    <div className="muted small">{m.email}</div>
                  </td>
                  <td>{roleLabel[m.orgRole]}</td>
                  <td>
                    {canManage ? (
                      <Select aria-label={`Rol de ${m.displayName}`} value={m.role} onChange={(e) => mutations.change.mutate({ userId: m.userId, role: e.target.value as ProjectRole })}>
                        <option value="Member">{projectRoleLabel.Member}</option>
                        <option value="Lead" disabled={m.orgRole === 'Viewer'}>
                          {projectRoleLabel.Lead}
                        </option>
                      </Select>
                    ) : (
                      <Badge tone={m.role === 'Lead' ? 'accent' : undefined}>{projectRoleLabel[m.role]}</Badge>
                    )}
                  </td>
                  {canManage ? (
                    <td style={{ textAlign: 'right' }}>
                      <Button size="sm" variant="ghost" aria-label={`Quitar a ${m.displayName}`} onClick={() => mutations.remove.mutate(m.userId)}>
                        <Trash2 size={14} />
                      </Button>
                    </td>
                  ) : null}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {canManage ? (
          <form
            className="row"
            onSubmit={(e) => {
              e.preventDefault();
              if (userId) mutations.add.mutate({ userId, role }, { onSuccess: () => setUserId('') });
            }}
          >
            <label className="sr-only" htmlFor="add-member">
              Miembro de la organización
            </label>
            <Select id="add-member" value={userId} onChange={(e) => setUserId(e.target.value)} style={{ maxWidth: 280 }}>
              <option value="">Añadir miembro de la organización…</option>
              {candidates.map((m) => (
                <option key={m.userId} value={m.userId}>
                  {m.displayName} ({roleLabel[m.role]})
                </option>
              ))}
            </Select>
            <label className="sr-only" htmlFor="add-member-role">
              Rol
            </label>
            <Select id="add-member-role" value={role} onChange={(e) => setRole(e.target.value as ProjectRole)} style={{ maxWidth: 160 }}>
              <option value="Member">{projectRoleLabel.Member}</option>
              <option value="Lead">{projectRoleLabel.Lead}</option>
            </Select>
            <Button type="submit" disabled={!userId} loading={mutations.add.isPending}>
              Añadir
            </Button>
          </form>
        ) : null}
        <InlineError error={error} />
      </div>
    </Card>
  );
}

function WorkflowSection() {
  const project = useProjectDetail();
  const { orgId } = useOrg();
  const toast = useToast();
  const mutations = useWorkflowMutations(orgId, project.id);
  const canEdit = project.permissions.canManage && project.status === 'Active';
  const statuses = useMemo(() => [...project.statuses].sort((a, b) => a.position - b.position), [project.statuses]);
  const [newStatus, setNewStatus] = useState({ name: '', category: 'InProgress' as StatusCategory });
  const error = mutations.addStatus.error ?? mutations.updateStatus.error ?? mutations.deleteStatus.error ?? mutations.setTransitions.error;
  const serverKey = project.transitions.map(transitionKey).sort().join(',');

  return (
    <Card title="Flujo de trabajo" description="Estados del proyecto y transiciones permitidas. Las tareas solo pueden moverse según esta matriz.">
      <div className="stack">
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Estado</th>
                <th scope="col">Categoría</th>
                {canEdit ? <th scope="col"><span className="sr-only">Acciones</span></th> : null}
              </tr>
            </thead>
            <tbody>
              {statuses.map((s) => (
                <tr key={s.id}>
                  <td>{s.name}</td>
                  <td>
                    {canEdit ? (
                      <Select
                        aria-label={`Categoría de ${s.name}`}
                        value={s.category}
                        onChange={(e) => mutations.updateStatus.mutate({ id: s.id, name: s.name, position: s.position, category: e.target.value as StatusCategory })}
                      >
                        {STATUS_CATEGORIES.map((c) => (
                          <option key={c} value={c}>
                            {categoryLabel[c]}
                          </option>
                        ))}
                      </Select>
                    ) : (
                      categoryLabel[s.category]
                    )}
                  </td>
                  {canEdit ? (
                    <td style={{ textAlign: 'right' }}>
                      <Button size="sm" variant="ghost" aria-label={`Eliminar estado ${s.name}`} onClick={() => mutations.deleteStatus.mutate(s.id, { onError: (e) => toast(errorMessage(e), 'error') })}>
                        <Trash2 size={14} />
                      </Button>
                    </td>
                  ) : null}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {canEdit ? (
          <form
            className="row"
            onSubmit={(e) => {
              e.preventDefault();
              mutations.addStatus.mutate(
                { name: newStatus.name, category: newStatus.category, position: statuses.length },
                { onSuccess: () => setNewStatus({ name: '', category: 'InProgress' }) },
              );
            }}
          >
            <label className="sr-only" htmlFor="new-status">
              Nuevo estado
            </label>
            <Input id="new-status" placeholder="Nuevo estado (p. ej. QA)" maxLength={40} value={newStatus.name} onChange={(e) => setNewStatus({ ...newStatus, name: e.target.value })} style={{ maxWidth: 240 }} />
            <label className="sr-only" htmlFor="new-status-category">
              Categoría
            </label>
            <Select id="new-status-category" value={newStatus.category} onChange={(e) => setNewStatus({ ...newStatus, category: e.target.value as StatusCategory })} style={{ maxWidth: 160 }}>
              {STATUS_CATEGORIES.map((c) => (
                <option key={c} value={c}>
                  {categoryLabel[c]}
                </option>
              ))}
            </Select>
            <Button type="submit" disabled={!newStatus.name.trim()} loading={mutations.addStatus.isPending}>
              Añadir estado
            </Button>
          </form>
        ) : null}

        {/* Keyed by the saved transitions so local edits reset whenever the server state changes. */}
        <TransitionMatrix
          key={serverKey}
          statuses={statuses}
          saved={project.transitions}
          canEdit={canEdit}
          saving={mutations.setTransitions.isPending}
          onSave={(t) => mutations.setTransitions.mutate(t, { onSuccess: () => toast('Transiciones guardadas') })}
        />
        <InlineError error={error} />
      </div>
    </Card>
  );
}

const transitionKey = (t: Transition) => `${t.fromStatusId}>${t.toStatusId}`;

function TransitionMatrix({
  statuses,
  saved,
  canEdit,
  onSave,
  saving,
}: {
  statuses: Status[];
  saved: Transition[];
  canEdit: boolean;
  onSave: (transitions: Transition[]) => void;
  saving: boolean;
}) {
  const [selected, setSelected] = useState(() => new Set(saved.map(transitionKey)));
  const dirty = selected.size !== saved.length || saved.some((t) => !selected.has(transitionKey(t)));

  const toggle = (from: string, to: string) => {
    const next = new Set(selected);
    const k = `${from}>${to}`;
    if (next.has(k)) next.delete(k);
    else next.add(k);
    setSelected(next);
  };

  return (
    <>
        <div className="table-wrap">
          <table className="matrix">
            <caption className="muted small" style={{ textAlign: 'left', paddingBottom: 6 }}>
              Transiciones permitidas: fila = estado de origen, columna = estado de destino.
            </caption>
            <thead>
              <tr>
                <th scope="col">Desde \ Hacia</th>
                {statuses.map((s) => (
                  <th key={s.id} scope="col">
                    {s.name}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {statuses.map((from) => (
                <tr key={from.id}>
                  <th scope="row">{from.name}</th>
                  {statuses.map((to) => (
                    <td key={to.id}>
                      {from.id === to.id ? (
                        <span className="muted" aria-hidden>
                          —
                        </span>
                      ) : (
                        <input
                          type="checkbox"
                          aria-label={`Permitir de ${from.name} a ${to.name}`}
                          checked={selected.has(`${from.id}>${to.id}`)}
                          disabled={!canEdit}
                          onChange={() => toggle(from.id, to.id)}
                        />
                      )}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {canEdit && dirty ? (
          <div className="row">
            <Button
              variant="primary"
              loading={saving}
              onClick={() =>
                onSave(
                  [...selected].map((k) => {
                    const [fromStatusId = '', toStatusId = ''] = k.split('>');
                    return { fromStatusId, toStatusId };
                  }),
                )
              }
            >
              Guardar transiciones
            </Button>
            <Button onClick={() => setSelected(new Set(saved.map(transitionKey)))}>Descartar</Button>
          </div>
        ) : null}
    </>
  );
}

function LabelsSection() {
  const project = useProjectDetail();
  const { orgId } = useOrg();
  const mutations = useLabelMutations(orgId, project.id);
  const [name, setName] = useState('');
  const [color, setColor] = useState('#4f46e5');
  const canCreate = project.permissions.canEditItems;
  const canDelete = project.permissions.canManage && project.status === 'Active';

  return (
    <Card title="Etiquetas">
      <div className="stack">
        <div className="row">
          {project.labels.length === 0 ? <span className="muted">Sin etiquetas.</span> : null}
          {project.labels.map((l) => (
            <span key={l.id} className="badge">
              <span className="dot" style={{ background: l.color }} aria-hidden />
              {l.name}
              {canDelete ? (
                <button className="btn btn-ghost btn-sm" style={{ height: 18, padding: '0 2px' }} aria-label={`Eliminar etiqueta ${l.name}`} onClick={() => mutations.remove.mutate(l.id)}>
                  ×
                </button>
              ) : null}
            </span>
          ))}
        </div>
        {canCreate ? (
          <form
            className="row"
            onSubmit={(e) => {
              e.preventDefault();
              mutations.create.mutate({ name, color }, { onSuccess: () => setName('') });
            }}
          >
            <label className="sr-only" htmlFor="label-name">
              Nombre de la etiqueta
            </label>
            <Input id="label-name" placeholder="Nombre (p. ej. backend)" maxLength={30} value={name} onChange={(e) => setName(e.target.value)} style={{ maxWidth: 220 }} />
            <label className="sr-only" htmlFor="label-color">
              Color
            </label>
            <input id="label-color" type="color" value={color} onChange={(e) => setColor(e.target.value)} style={{ width: 44, height: 36, border: 0, background: 'none' }} />
            <Button type="submit" disabled={!name.trim()} loading={mutations.create.isPending}>
              Crear etiqueta
            </Button>
          </form>
        ) : null}
        <InlineError error={mutations.create.error ?? mutations.remove.error} />
      </div>
    </Card>
  );
}

function DangerZone() {
  const project = useProjectDetail();
  const { orgId, path } = useOrg();
  const navigate = useNavigate();
  const toast = useToast();
  const action = useProjectAction(orgId, project.id);
  const [confirm, setConfirm] = useState<'archive' | 'restore' | 'delete' | null>(null);
  if (!project.permissions.canManage) return null;
  const archived = project.status === 'Archived';

  return (
    <Card title="Zona de riesgo">
      <div className="stack">
        <div className="row-between">
          <div>
            <strong>{archived ? 'Restaurar proyecto' : 'Archivar proyecto'}</strong>
            <p className="muted small">{archived ? 'Vuelve a permitir cambios en el proyecto.' : 'El proyecto queda en solo lectura. Se puede restaurar.'}</p>
          </div>
          <Button onClick={() => setConfirm(archived ? 'restore' : 'archive')}>{archived ? 'Restaurar' : 'Archivar'}</Button>
        </div>
        {project.permissions.canDelete ? (
          <div className="row-between">
            <div>
              <strong>Eliminar proyecto</strong>
              <p className="muted small">Elimina el proyecto y todas sus tareas de forma permanente. Primero hay que archivarlo.</p>
            </div>
            <Button variant="danger" disabled={!archived} onClick={() => setConfirm('delete')}>
              Eliminar
            </Button>
          </div>
        ) : null}
      </div>
      <ConfirmDialog
        open={confirm !== null}
        title={confirm === 'delete' ? `Eliminar ${project.name}` : confirm === 'archive' ? 'Archivar proyecto' : 'Restaurar proyecto'}
        destructive={confirm === 'delete'}
        confirmLabel={confirm === 'delete' ? 'Eliminar definitivamente' : 'Confirmar'}
        message={
          confirm === 'delete'
            ? `Se eliminarán ${project.totalItems} tarea(s), comentarios e historial. Esta acción no se puede deshacer.`
            : confirm === 'archive'
              ? 'Nadie podrá crear ni modificar tareas hasta que se restaure.'
              : 'El proyecto volverá a estar activo.'
        }
        loading={action.isPending}
        error={action.error}
        onCancel={() => setConfirm(null)}
        onConfirm={() =>
          confirm &&
          action.mutate(confirm, {
            onSuccess: () => {
              setConfirm(null);
              if (confirm === 'delete') {
                toast('Proyecto eliminado');
                navigate(path('/projects'));
              }
            },
          })
        }
      />
    </Card>
  );
}
