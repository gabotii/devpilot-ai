import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useNavigate } from 'react-router';
import { Bug, CheckSquare, CornerDownRight } from 'lucide-react';
import { useCreateWorkItem } from '../../api/hooks';
import type { Priority, ProjectDetail, StatusCategory, WorkItemType } from '../../api/types';
import { useOrg } from '../../app/providers';
import { Badge, Button, Dialog, Field, InlineError, Input, Select, Textarea } from '../../components/ui';
import { categoryLabel, categoryTone, PRIORITIES, priorityColor, priorityLabel, typeLabel } from '../../lib/format';
import { applyServerErrors } from '../../lib/forms';

export function PriorityTag({ priority }: { priority: Priority }) {
  return (
    <span className="priority" title={`Prioridad ${priorityLabel[priority]}`}>
      <span className="dot" style={{ background: priorityColor[priority] }} aria-hidden />
      {priorityLabel[priority]}
    </span>
  );
}

export function TypeIcon({ type }: { type: WorkItemType }) {
  const Icon = type === 'Issue' ? Bug : type === 'Subtask' ? CornerDownRight : CheckSquare;
  return (
    <span title={typeLabel[type]} style={{ display: 'inline-flex', color: type === 'Issue' ? 'var(--danger)' : 'var(--accent)' }}>
      <Icon size={15} aria-label={typeLabel[type]} />
    </span>
  );
}

export function StatusBadge({ name, category }: { name: string; category: StatusCategory }) {
  return (
    <Badge tone={categoryTone[category]} title={categoryLabel[category]}>
      {name}
    </Badge>
  );
}

const itemSchema = z.object({
  type: z.enum(['Task', 'Issue', 'Subtask']),
  title: z.string().trim().min(1, 'El título es obligatorio.').max(200),
  description: z.string().max(20000).optional(),
  priority: z.enum(['Low', 'Medium', 'High', 'Critical']),
  assigneeId: z.string().optional(),
  dueDate: z.string().optional(),
  statusId: z.string().optional(),
  labelIds: z.array(z.string()).max(10),
});
type ItemValues = z.infer<typeof itemSchema>;

interface CreateItemDialogProps {
  open: boolean;
  onClose: () => void;
  project: ProjectDetail;
  defaultStatusId?: string;
  parent?: { id: string; key: string };
}

export function CreateWorkItemDialog({ open, onClose, project, defaultStatusId, parent }: CreateItemDialogProps) {
  const { orgId, path } = useOrg();
  const navigate = useNavigate();
  const create = useCreateWorkItem(orgId, project.id);
  const openStatuses = project.statuses.filter((s) => s.category === 'Todo' || s.category === 'InProgress');
  const defaults: ItemValues = {
    type: parent ? 'Subtask' : 'Task',
    title: '',
    description: '',
    priority: 'Medium',
    assigneeId: '',
    dueDate: '',
    statusId: defaultStatusId ?? '',
    labelIds: [],
  };
  const form = useForm<ItemValues>({ resolver: zodResolver(itemSchema), defaultValues: defaults });
  const { reset } = form;

  // Start from a clean form every time the dialog opens (e.g. "add card" in a different column).
  useEffect(() => {
    if (open) reset(defaults);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- defaults derive from the props below
  }, [open, defaultStatusId, parent?.id, reset]);

  const onSubmit = form.handleSubmit(async (v) => {
    try {
      const created = await create.mutateAsync({
        type: v.type,
        title: v.title,
        description: v.description || undefined,
        priority: v.priority,
        assigneeId: v.assigneeId || null,
        dueDate: v.dueDate || null,
        statusId: v.statusId || null,
        parentId: parent?.id ?? null,
        labelIds: v.labelIds,
      });
      form.reset();
      onClose();
      if (!parent) navigate(path(`/projects/${project.id}/items/${created.item.number}`));
    } catch (error) {
      applyServerErrors(error, form.setError, ['title', 'description', 'priority', 'assigneeId', 'dueDate', 'statusId', 'labelIds']);
    }
  });

  const errors = form.formState.errors;
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={parent ? `Nueva subtarea de ${parent.key}` : 'Nueva tarea'}
      wide
      footer={
        <>
          <Button onClick={onClose}>Cancelar</Button>
          <Button variant="primary" type="submit" form="create-item" loading={form.formState.isSubmitting}>
            Crear
          </Button>
        </>
      }
    >
      <form id="create-item" className="stack" onSubmit={onSubmit} noValidate>
        <div className="grid grid-2" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))' }}>
          {!parent ? (
            <Field label="Tipo">
              {(p) => (
                <Select {...p} {...form.register('type')}>
                  <option value="Task">{typeLabel.Task}</option>
                  <option value="Issue">{typeLabel.Issue}</option>
                </Select>
              )}
            </Field>
          ) : null}
          <Field label="Prioridad">
            {(p) => (
              <Select {...p} {...form.register('priority')}>
                {PRIORITIES.map((pr) => (
                  <option key={pr} value={pr}>
                    {priorityLabel[pr]}
                  </option>
                ))}
              </Select>
            )}
          </Field>
          <Field label="Estado inicial" error={errors.statusId?.message}>
            {(p) => (
              <Select {...p} {...form.register('statusId')}>
                <option value="">Por defecto</option>
                {openStatuses.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        </div>
        <Field label="Título" error={errors.title?.message}>
          {(p) => <Input {...p} autoFocus maxLength={200} {...form.register('title')} />}
        </Field>
        <Field label="Descripción" error={errors.description?.message}>
          {(p) => <Textarea {...p} rows={5} {...form.register('description')} />}
        </Field>
        <div className="grid grid-2" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))' }}>
          <Field label="Responsable" error={errors.assigneeId?.message} hint="Solo miembros del proyecto.">
            {(p) => (
              <Select {...p} {...form.register('assigneeId')}>
                <option value="">Sin asignar</option>
                {project.members.map((m) => (
                  <option key={m.userId} value={m.userId}>
                    {m.displayName}
                  </option>
                ))}
              </Select>
            )}
          </Field>
          <Field label="Fecha límite" error={errors.dueDate?.message}>
            {(p) => <Input {...p} type="date" {...form.register('dueDate')} />}
          </Field>
        </div>
        {project.labels.length > 0 ? (
          <fieldset className="stack-sm" style={{ border: 0, padding: 0, margin: 0 }}>
            <legend className="field-label">Etiquetas</legend>
            <div className="row">
              {project.labels.map((l) => (
                <label key={l.id} className="badge" style={{ cursor: 'pointer' }}>
                  <input type="checkbox" value={l.id} {...form.register('labelIds')} />
                  <span className="dot" style={{ background: l.color }} aria-hidden />
                  {l.name}
                </label>
              ))}
            </div>
            {errors.labelIds ? <span className="field-error">Máximo 10 etiquetas.</span> : null}
          </fieldset>
        ) : null}
        <InlineError error={create.error && !Object.keys(errors).length ? create.error : null} />
      </form>
    </Dialog>
  );
}
