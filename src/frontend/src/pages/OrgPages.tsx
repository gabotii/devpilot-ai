import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { Check, Copy, Trash2 } from 'lucide-react';
import { ApiError } from '../api/client';
import {
  useActivity,
  useChangeMemberRole,
  useChangePassword,
  useCreateInvitation,
  useInvitations,
  useMembers,
  useNotificationMutations,
  useNotifications,
  useOrganization,
  useRemoveMember,
  useRevokeInvitation,
  useUpdateOrganization,
  useUpdateProfile,
} from '../api/hooks';
import type { InvitationCreated, OrgRole } from '../api/types';
import { ORG_ROLE_RANK } from '../api/types';
import { useAuth, useOrg, useToast } from '../app/providers';
import { ActivityList } from '../components/ActivityList';
import { SearchInput } from '../components/SearchInput';
import { Avatar, Badge, Button, Card, ConfirmDialog, EmptyState, ErrorState, Field, InlineError, Input, LoadingState, Pagination, Select } from '../components/ui';
import { formatDate, formatRelative, ORG_ROLES, roleLabel } from '../lib/format';

export function ActivityPage() {
  const { orgId } = useOrg();
  const [page, setPage] = useState(1);
  const activity = useActivity(orgId, undefined, page);
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Actividad</h1>
          <p>Lo que ha pasado en la organización.</p>
        </div>
      </div>
      <Card>
        {activity.isPending ? (
          <LoadingState />
        ) : activity.error ? (
          <ErrorState error={activity.error} onRetry={() => void activity.refetch()} />
        ) : (
          <div className="stack">
            <ActivityList items={activity.data.items} />
            <Pagination page={activity.data.page} totalPages={activity.data.totalPages} totalCount={activity.data.totalCount} onPage={setPage} />
          </div>
        )}
      </Card>
    </div>
  );
}

export function NotificationsPage() {
  const { orgId, path } = useOrg();
  const [unread, setUnread] = useState(false);
  const [page, setPage] = useState(1);
  const list = useNotifications(orgId, unread, page);
  const { markRead, markAll } = useNotificationMutations(orgId);
  const navigate = useNavigate();

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Notificaciones</h1>
          <p>Asignaciones, fallos de pipelines, despliegues e incidencias.</p>
        </div>
        <div className="row">
          <label className="row small" style={{ gap: 6 }}>
            <input type="checkbox" checked={unread} onChange={(e) => { setUnread(e.target.checked); setPage(1); }} /> Solo no leídas
          </label>
          <Button onClick={() => markAll.mutate()} loading={markAll.isPending} disabled={!list.data?.unreadCount}>
            Marcar todas como leídas
          </Button>
        </div>
      </div>
      <Card bodyClassName="">
        {list.isPending ? (
          <LoadingState />
        ) : list.error ? (
          <ErrorState error={list.error} onRetry={() => void list.refetch()} />
        ) : list.data.page.items.length === 0 ? (
          <EmptyState title={unread ? 'Todo al día' : 'Sin notificaciones'} />
        ) : (
          <>
            <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
              {list.data.page.items.map((n) => {
                const link = n.link?.startsWith('/') ? n.link : null;
                return (
                <li key={n.id} className="row-between" style={{ padding: '12px 20px', borderBottom: '1px solid var(--border)', background: n.readAt ? undefined : 'var(--accent-soft)' }}>
                  <div style={{ minWidth: 0 }}>
                    <div className="row" style={{ gap: 6 }}>
                      {!n.readAt ? <span className="dot" style={{ background: 'var(--accent)' }} aria-label="No leída" /> : null}
                      <strong>{n.title}</strong>
                    </div>
                    {n.body ? <div className="muted small truncate">{n.body}</div> : null}
                    <time className="muted small" dateTime={n.createdAt}>
                      {formatRelative(n.createdAt)}
                    </time>
                  </div>
                  <div className="row">
                    {link ? (
                      <Button
                        size="sm"
                        onClick={() => {
                          if (!n.readAt) markRead.mutate(n.id);
                          navigate(path(link));
                        }}
                      >
                        Abrir
                      </Button>
                    ) : null}
                    {!n.readAt ? (
                      <Button size="sm" variant="ghost" onClick={() => markRead.mutate(n.id)} aria-label={`Marcar "${n.title}" como leída`}>
                        <Check size={14} />
                      </Button>
                    ) : null}
                  </div>
                </li>
                );
              })}
            </ul>
            <Pagination page={list.data.page.page} totalPages={list.data.page.totalPages} totalCount={list.data.page.totalCount} onPage={setPage} />
          </>
        )}
      </Card>
    </div>
  );
}

export function OrgSettingsPage() {
  const { orgId, hasRole, path } = useOrg();
  const org = useOrganization(orgId);
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Configuración</h1>
          <p>Organización, miembros, roles e invitaciones.</p>
        </div>
        {hasRole('Admin') ? (
          <Link className="btn" to={path('/audit')}>
            Ver auditoría
          </Link>
        ) : null}
      </div>
      {org.isPending ? <LoadingState /> : org.error ? <ErrorState error={org.error} /> : <OrgGeneral name={org.data.name} retention={org.data.auditRetentionDays} slug={org.data.slug} />}
      <MembersCard />
      {hasRole('Admin') ? <InvitationsCard /> : null}
    </div>
  );
}

function OrgGeneral({ name: initialName, retention: initialRetention, slug }: { name: string; retention: number; slug: string }) {
  const { orgId, hasRole } = useOrg();
  const toast = useToast();
  const update = useUpdateOrganization(orgId);
  const [name, setName] = useState(initialName);
  const [retention, setRetention] = useState(initialRetention);
  const canEdit = hasRole('Admin');
  return (
    <Card title="Organización">
      <form
        className="stack"
        onSubmit={(e) => {
          e.preventDefault();
          update.mutate({ name, auditRetentionDays: retention }, { onSuccess: () => toast('Organización actualizada') });
        }}
      >
        <Field label="Nombre">{(p) => <Input {...p} value={name} disabled={!canEdit} onChange={(e) => setName(e.target.value)} />}</Field>
        <Field label="Identificador">{(p) => <Input {...p} value={slug} readOnly disabled />}</Field>
        <Field label="Retención de auditoría (días)" hint="Entre 30 y 3650. Solo el propietario puede cambiarla. Los registros más antiguos se eliminan automáticamente.">
          {(p) => <Input {...p} type="number" min={30} max={3650} value={retention} disabled={!hasRole('Owner')} onChange={(e) => setRetention(Number(e.target.value))} />}
        </Field>
        <InlineError error={update.error} />
        {canEdit ? (
          <div>
            <Button type="submit" variant="primary" loading={update.isPending}>
              Guardar
            </Button>
          </div>
        ) : null}
      </form>
    </Card>
  );
}

function MembersCard() {
  const me = useAuth();
  const { orgId, role: myRole, hasRole } = useOrg();
  const navigate = useNavigate();
  const toast = useToast();
  const [q, setQ] = useState('');
  const members = useMembers(orgId, q || undefined);
  const changeRole = useChangeMemberRole(orgId);
  const remove = useRemoveMember(orgId);
  const [removing, setRemoving] = useState<{ userId: string; name: string } | null>(null);

  const canManage = (targetRole: OrgRole) => myRole === 'Owner' || (myRole === 'Admin' && ORG_ROLE_RANK[targetRole] < ORG_ROLE_RANK.Admin);
  const assignable = (ORG_ROLES as OrgRole[]).filter((r) => myRole === 'Owner' || ORG_ROLE_RANK[r] < ORG_ROLE_RANK.Admin);

  return (
    <Card title="Miembros" actions={<SearchInput label="Buscar miembros" placeholder="Buscar…" value={q} onChange={setQ} />}>
      {members.isPending ? (
        <LoadingState />
      ) : members.error ? (
        <ErrorState error={members.error} />
      ) : (
        <div className="stack">
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th scope="col">Miembro</th>
                  <th scope="col">Rol</th>
                  <th scope="col">Desde</th>
                  <th scope="col"><span className="sr-only">Acciones</span></th>
                </tr>
              </thead>
              <tbody>
                {members.data.items.map((m) => {
                  const self = m.userId === me.user.id;
                  return (
                    <tr key={m.userId}>
                      <td>
                        <span className="row" style={{ gap: 8 }}>
                          <Avatar name={m.displayName} />
                          <span>
                            {m.displayName} {self ? <Badge>Tú</Badge> : null}
                            <div className="muted small">{m.email}</div>
                          </span>
                        </span>
                      </td>
                      <td>
                        {!self && hasRole('Admin') && canManage(m.role) ? (
                          <Select aria-label={`Rol de ${m.displayName}`} value={m.role} onChange={(e) => changeRole.mutate({ userId: m.userId, role: e.target.value as OrgRole }, { onSuccess: () => toast('Rol actualizado') })} style={{ maxWidth: 180 }}>
                            {assignable.map((r) => (
                              <option key={r} value={r}>
                                {roleLabel[r]}
                              </option>
                            ))}
                          </Select>
                        ) : (
                          <Badge tone={m.role === 'Owner' ? 'accent' : undefined}>{roleLabel[m.role]}</Badge>
                        )}
                      </td>
                      <td className="small muted">{formatDate(m.joinedAt)}</td>
                      <td style={{ textAlign: 'right' }}>
                        {self ? (
                          <Button size="sm" onClick={() => setRemoving({ userId: m.userId, name: 'ti' })}>
                            Salir
                          </Button>
                        ) : hasRole('Admin') && canManage(m.role) ? (
                          <Button size="sm" variant="ghost" aria-label={`Quitar a ${m.displayName}`} onClick={() => setRemoving({ userId: m.userId, name: m.displayName })}>
                            <Trash2 size={14} />
                          </Button>
                        ) : null}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <InlineError error={changeRole.error} />
        </div>
      )}
      <ConfirmDialog
        open={removing !== null}
        title={removing?.userId === me.user.id ? 'Salir de la organización' : 'Quitar miembro'}
        destructive
        confirmLabel={removing?.userId === me.user.id ? 'Salir' : 'Quitar'}
        message={
          removing?.userId === me.user.id
            ? 'Perderás el acceso a todos los proyectos de esta organización.'
            : `${removing?.name} perderá el acceso a la organización y a sus proyectos.`
        }
        loading={remove.isPending}
        error={remove.error}
        onCancel={() => setRemoving(null)}
        onConfirm={() =>
          removing &&
          remove.mutate(removing.userId, {
            onSuccess: () => {
              const self = removing.userId === me.user.id;
              setRemoving(null);
              if (self) navigate('/');
            },
          })
        }
      />
    </Card>
  );
}

function InvitationsCard() {
  const { orgId, role: myRole } = useOrg();
  const invitations = useInvitations(orgId, true);
  const create = useCreateInvitation(orgId);
  const revoke = useRevokeInvitation(orgId);
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<OrgRole>('Developer');
  const [created, setCreated] = useState<InvitationCreated | null>(null);
  const [copied, setCopied] = useState(false);
  const roles = (['Admin', 'Developer', 'Viewer'] as OrgRole[]).filter((r) => myRole === 'Owner' || r !== 'Admin');
  const link = created ? `${window.location.origin}/invite#${created.acceptToken}` : '';

  return (
    <Card title="Invitaciones" description="El enlace de invitación se muestra una sola vez. Compártelo por un canal seguro.">
      <div className="stack">
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault();
            setCopied(false);
            create.mutate({ email, role }, { onSuccess: (r) => { setCreated(r); setEmail(''); } });
          }}
        >
          <label className="sr-only" htmlFor="invite-email">
            Email
          </label>
          <Input id="invite-email" type="email" required placeholder="persona@empresa.com" value={email} onChange={(e) => setEmail(e.target.value)} style={{ maxWidth: 280 }} />
          <label className="sr-only" htmlFor="invite-role">
            Rol
          </label>
          <Select id="invite-role" value={role} onChange={(e) => setRole(e.target.value as OrgRole)} style={{ maxWidth: 180 }}>
            {roles.map((r) => (
              <option key={r} value={r}>
                {roleLabel[r]}
              </option>
            ))}
          </Select>
          <Button type="submit" variant="primary" loading={create.isPending} disabled={!email}>
            Invitar
          </Button>
        </form>
        {create.error ? (
          <InlineError error={create.error instanceof ApiError && create.error.status === 400 ? new Error(Object.values(create.error.fieldErrors)[0]?.[0] ?? create.error.message) : create.error} />
        ) : null}
        {created ? (
          <div className="alert alert-success stack-sm" role="status" style={{ flexDirection: 'column' }}>
            <strong>Invitación creada para {created.invitation.email}</strong>
            <span className="small">
              {created.emailSent ? 'Se ha enviado un email.' : 'No hay un canal de email configurado: copia este enlace y compártelo. Caduca en 7 días.'}
            </span>
            <div className="row" style={{ width: '100%' }}>
              <Input readOnly value={link} aria-label="Enlace de invitación" onFocus={(e) => e.target.select()} style={{ flex: 1 }} />
              <Button
                onClick={() => {
                  void navigator.clipboard?.writeText(link).then(() => setCopied(true));
                }}
              >
                {copied ? <Check size={14} aria-hidden /> : <Copy size={14} aria-hidden />} {copied ? 'Copiado' : 'Copiar'}
              </Button>
            </div>
          </div>
        ) : null}
        {invitations.isPending ? (
          <LoadingState />
        ) : invitations.error ? (
          <ErrorState error={invitations.error} />
        ) : invitations.data.length === 0 ? (
          <p className="muted">No hay invitaciones.</p>
        ) : (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th scope="col">Email</th>
                  <th scope="col">Rol</th>
                  <th scope="col">Estado</th>
                  <th scope="col">Caduca</th>
                  <th scope="col"><span className="sr-only">Acciones</span></th>
                </tr>
              </thead>
              <tbody>
                {invitations.data.map((i) => (
                  <tr key={i.id}>
                    <td>{i.email}</td>
                    <td>{roleLabel[i.role]}</td>
                    <td>
                      <Badge tone={i.status === 'Pending' ? 'info' : i.status === 'Accepted' ? 'success' : undefined}>
                        {{ Pending: 'Pendiente', Accepted: 'Aceptada', Revoked: 'Revocada', Expired: 'Caducada' }[i.status]}
                      </Badge>
                    </td>
                    <td className="small muted">{formatDate(i.expiresAt)}</td>
                    <td style={{ textAlign: 'right' }}>
                      {i.status === 'Pending' ? (
                        <Button size="sm" variant="ghost" onClick={() => revoke.mutate(i.id)}>
                          Revocar
                        </Button>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </Card>
  );
}

export function ProfilePage() {
  const me = useAuth();
  const toast = useToast();
  const profile = useUpdateProfile();
  const password = useChangePassword();
  const [name, setName] = useState(me.user.displayName);
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const fieldError = (field: string) => (password.error instanceof ApiError ? password.error.fieldErrors[field]?.[0] : undefined);

  return (
    <div className="auth-layout" style={{ placeItems: 'start center' }}>
      <main className="page" style={{ width: '100%', maxWidth: 640 }}>
        <div className="page-header">
          <div>
            <h1>Perfil y seguridad</h1>
            <p>{me.user.email}</p>
          </div>
          <Link className="btn" to="/">
            Volver
          </Link>
        </div>
        <Card title="Perfil">
          <form className="stack" onSubmit={(e) => { e.preventDefault(); profile.mutate({ displayName: name }, { onSuccess: () => toast('Perfil actualizado') }); }}>
            <Field label="Nombre visible">{(p) => <Input {...p} value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />}</Field>
            <InlineError error={profile.error} />
            <div>
              <Button type="submit" variant="primary" loading={profile.isPending} disabled={!name.trim()}>
                Guardar
              </Button>
            </div>
          </form>
        </Card>
        <Card title="Cambiar contraseña" description="Se cerrarán todas tus otras sesiones abiertas.">
          <form
            className="stack"
            onSubmit={(e) => {
              e.preventDefault();
              password.mutate({ currentPassword: current, newPassword: next }, { onSuccess: () => { setCurrent(''); setNext(''); toast('Contraseña cambiada. Otras sesiones cerradas.'); } });
            }}
          >
            <Field label="Contraseña actual" error={fieldError('currentPassword')}>
              {(p) => <Input {...p} type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />}
            </Field>
            <Field label="Nueva contraseña" error={fieldError('newPassword')} hint="Al menos 12 caracteres.">
              {(p) => <Input {...p} type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />}
            </Field>
            {password.error && !(password.error instanceof ApiError && password.error.status === 400) ? <InlineError error={password.error} /> : null}
            <div>
              <Button type="submit" variant="primary" loading={password.isPending} disabled={!current || next.length < 12}>
                Cambiar contraseña
              </Button>
            </div>
          </form>
        </Card>
      </main>
    </div>
  );
}
