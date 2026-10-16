import { useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { FolderKanban, Plus } from 'lucide-react';
import { ApiError } from '../../api/client';
import { useCreateProject, useProjects } from '../../api/hooks';
import type { ProjectStatus } from '../../api/types';
import { useOrg } from '../../app/providers';
import { Badge, Button, Dialog, EmptyState, ErrorState, Field, InlineError, Input, LoadingState, Pagination, Select, Textarea } from '../../components/ui';
import { SearchInput } from '../../components/SearchInput';
import { formatRelative, projectRoleLabel } from '../../lib/format';
import { applyServerErrors } from '../../lib/forms';

const projectSchema = z.object({
  name: z.string().trim().min(2, 'Mínimo 2 caracteres.').max(100),
  key: z
    .string()
    .trim()
    .regex(/^[A-Za-z][A-Za-z0-9]{1,9}$/, '2-10 letras o números, empezando por letra (p. ej. API).'),
  description: z.string().max(4000).optional(),
});
type ProjectValues = z.infer<typeof projectSchema>;

function suggestKey(name: string): string {
  const words = name.normalize('NFD').replace(/[^A-Za-z0-9 ]/g, '').trim().split(/\s+/).filter(Boolean);
  const key = words.length > 1 ? words.map((w) => w[0]).join('') : (words[0] ?? '').slice(0, 4);
  return key.toUpperCase().replace(/^[0-9]+/, '').slice(0, 10);
}

export function CreateProjectDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { orgId, path } = useOrg();
  const navigate = useNavigate();
  const create = useCreateProject(orgId);
  const form = useForm<ProjectValues>({ resolver: zodResolver(projectSchema), defaultValues: { name: '', key: '', description: '' } });
  const keyTouched = form.formState.dirtyFields.key;

  const onSubmit = form.handleSubmit(async (values) => {
    try {
      const project = await create.mutateAsync(values);
      form.reset();
      onClose();
      navigate(path(`/projects/${project.id}`));
    } catch (error) {
      if (error instanceof ApiError && error.code === 'project.key_taken') form.setError('key', { message: 'Esta clave ya está en uso.' });
      else applyServerErrors(error, form.setError, ['name', 'key', 'description']);
    }
  });

  const errors = form.formState.errors;
  return (
    <Dialog
      open={open}
      title="Nuevo proyecto"
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancelar</Button>
          <Button variant="primary" type="submit" form="create-project" loading={form.formState.isSubmitting}>
            Crear proyecto
          </Button>
        </>
      }
    >
      <form id="create-project" className="stack" onSubmit={onSubmit} noValidate>
        <Field label="Nombre" error={errors.name?.message}>
          {(p) => (
            <Input
              {...p}
              autoFocus
              {...form.register('name', {
                onChange: (e: React.ChangeEvent<HTMLInputElement>) => {
                  if (!keyTouched) form.setValue('key', suggestKey(e.target.value));
                },
              })}
            />
          )}
        </Field>
        <Field label="Clave" error={errors.key?.message} hint="Prefijo de las tareas (API-12). No se puede cambiar después.">
          {(p) => <Input {...p} style={{ textTransform: 'uppercase' }} maxLength={10} {...form.register('key')} />}
        </Field>
        <Field label="Descripción (opcional)" error={errors.description?.message}>
          {(p) => <Textarea {...p} rows={3} {...form.register('description')} />}
        </Field>
        <InlineError error={create.error instanceof ApiError && create.error.status !== 400 && create.error.code !== 'project.key_taken' ? create.error : null} />
      </form>
    </Dialog>
  );
}

export function ProjectsPage() {
  const { orgId, path, hasRole } = useOrg();
  const [params, setParams] = useSearchParams();
  const [creating, setCreating] = useState(false);
  const q = params.get('q') ?? '';
  const status = (params.get('status') as ProjectStatus | null) ?? 'Active';
  const page = Number(params.get('page') ?? '1');
  const projects = useProjects(orgId, { q: q || undefined, status, page, sort: params.get('sort') ?? undefined });

  const update = (changes: Record<string, string>) => {
    const next = new URLSearchParams(params);
    for (const [k, v] of Object.entries(changes)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    if (!('page' in changes)) next.delete('page');
    setParams(next, { replace: true });
  };

  const canCreate = hasRole('Developer');

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Proyectos</h1>
          <p>Todos los proyectos de la organización.</p>
        </div>
        {canCreate ? (
          <Button variant="primary" onClick={() => setCreating(true)}>
            <Plus size={16} aria-hidden /> Nuevo proyecto
          </Button>
        ) : null}
      </div>

      <div className="toolbar" role="search">
        <SearchInput label="Buscar proyectos" placeholder="Buscar por nombre o clave…" value={q} onChange={(v) => update({ q: v })} />
        <label className="sr-only" htmlFor="project-status">
          Estado
        </label>
        <Select id="project-status" value={status} onChange={(e) => update({ status: e.target.value })}>
          <option value="Active">Activos</option>
          <option value="Archived">Archivados</option>
        </Select>
        <label className="sr-only" htmlFor="project-sort">
          Ordenar
        </label>
        <Select id="project-sort" value={params.get('sort') ?? '-updatedAt'} onChange={(e) => update({ sort: e.target.value })}>
          <option value="-updatedAt">Actualizados recientemente</option>
          <option value="name">Nombre (A-Z)</option>
          <option value="-createdAt">Más nuevos</option>
        </Select>
      </div>

      {projects.isPending ? (
        <LoadingState />
      ) : projects.error ? (
        <ErrorState error={projects.error} onRetry={() => void projects.refetch()} />
      ) : projects.data.items.length === 0 ? (
        <div className="card">
          <EmptyState
            icon={<FolderKanban size={28} aria-hidden />}
            title={q ? 'Ningún proyecto coincide' : status === 'Archived' ? 'No hay proyectos archivados' : 'Aún no hay proyectos'}
            description={canCreate && !q && status === 'Active' ? 'Crea el primero para organizar el trabajo de tu equipo.' : undefined}
            action={
              canCreate && !q && status === 'Active' ? (
                <Button variant="primary" onClick={() => setCreating(true)}>
                  Crear proyecto
                </Button>
              ) : undefined
            }
          />
        </div>
      ) : (
        <>
          <div className="grid grid-cards" style={{ opacity: projects.isPlaceholderData ? 0.6 : 1 }}>
            {projects.data.items.map((p) => (
              <Link key={p.id} to={path(`/projects/${p.id}`)} className="card card-body stack-sm" style={{ color: 'inherit', textDecoration: 'none' }}>
                <div className="row-between">
                  <span className="badge mono">{p.key}</span>
                  <div className="row" style={{ gap: 6 }}>
                    {p.myRole ? <Badge tone="accent">{projectRoleLabel[p.myRole]}</Badge> : null}
                    {p.status === 'Archived' ? <Badge>Archivado</Badge> : null}
                  </div>
                </div>
                <h3>{p.name}</h3>
                <p className="muted small" style={{ minHeight: 20 }}>
                  {p.description ?? 'Sin descripción'}
                </p>
                <div className="row muted small">
                  <span>
                    <strong style={{ color: 'var(--text)' }}>{p.openItems}</strong> abiertas
                  </span>
                  <span>·</span>
                  <span>{p.totalItems} en total</span>
                  <span>·</span>
                  <span>
                    {p.memberCount} miembro{p.memberCount === 1 ? '' : 's'}
                  </span>
                </div>
                <span className="muted small">Actualizado {formatRelative(p.updatedAt)}</span>
              </Link>
            ))}
          </div>
          <div className="card">
            <Pagination page={projects.data.page} totalPages={projects.data.totalPages} totalCount={projects.data.totalCount} onPage={(n) => update({ page: String(n) })} />
          </div>
        </>
      )}

      <CreateProjectDialog open={creating} onClose={() => setCreating(false)} />
    </div>
  );
}
