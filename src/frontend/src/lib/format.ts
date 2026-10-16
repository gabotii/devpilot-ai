import type { OrgRole, Priority, ProjectRole, StatusCategory, WorkItemType } from '../api/types';

export const priorityLabel: Record<Priority, string> = { Critical: 'Crítica', High: 'Alta', Medium: 'Media', Low: 'Baja' };
export const priorityColor: Record<Priority, string> = {
  Critical: 'var(--priority-critical)',
  High: 'var(--priority-high)',
  Medium: 'var(--priority-medium)',
  Low: 'var(--priority-low)',
};
export const PRIORITIES: Priority[] = ['Critical', 'High', 'Medium', 'Low'];

export const typeLabel: Record<WorkItemType, string> = { Task: 'Tarea', Issue: 'Incidencia', Subtask: 'Subtarea' };
export const WORK_ITEM_TYPES: WorkItemType[] = ['Task', 'Issue', 'Subtask'];

export const categoryLabel: Record<StatusCategory, string> = {
  Todo: 'Por hacer',
  InProgress: 'En curso',
  Done: 'Completado',
  Cancelled: 'Cancelado',
};
export const categoryTone: Record<StatusCategory, 'info' | 'accent' | 'success' | undefined> = {
  Todo: undefined,
  InProgress: 'accent',
  Done: 'success',
  Cancelled: undefined,
};
export const STATUS_CATEGORIES: StatusCategory[] = ['Todo', 'InProgress', 'Done', 'Cancelled'];

export const roleLabel: Record<OrgRole, string> = { Owner: 'Propietario', Admin: 'Administrador', Developer: 'Desarrollador', Viewer: 'Lector' };
export const ORG_ROLES: OrgRole[] = ['Owner', 'Admin', 'Developer', 'Viewer'];
export const projectRoleLabel: Record<ProjectRole, string> = { Lead: 'Líder', Member: 'Miembro' };

const dateFormatter = new Intl.DateTimeFormat('es', { day: 'numeric', month: 'short', year: 'numeric' });
const dateTimeFormatter = new Intl.DateTimeFormat('es', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
const relativeFormatter = new Intl.RelativeTimeFormat('es', { numeric: 'auto' });

export function formatDate(value: string | null | undefined): string {
  if (!value) return '—';
  // Date-only values (yyyy-MM-dd) are calendar dates, not instants: parse as local to avoid timezone shifts.
  const date = /^\d{4}-\d{2}-\d{2}$/.test(value) ? new Date(`${value}T00:00:00`) : new Date(value);
  return dateFormatter.format(date);
}

export function formatDateTime(value: string | null | undefined): string {
  return value ? dateTimeFormatter.format(new Date(value)) : '—';
}

export function formatRelative(value: string, now: Date = new Date()): string {
  const seconds = Math.round((new Date(value).getTime() - now.getTime()) / 1000);
  const abs = Math.abs(seconds);
  if (abs < 60) return relativeFormatter.format(seconds, 'second');
  if (abs < 3600) return relativeFormatter.format(Math.round(seconds / 60), 'minute');
  if (abs < 86400) return relativeFormatter.format(Math.round(seconds / 3600), 'hour');
  if (abs < 86400 * 30) return relativeFormatter.format(Math.round(seconds / 86400), 'day');
  return formatDate(value);
}

export function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined) return '—';
  if (seconds < 60) return `${Math.round(seconds)} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} min ${Math.round(seconds % 60)} s`;
  return `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

export function isOverdue(dueDate: string | null, category: StatusCategory, today: Date = new Date()): boolean {
  if (!dueDate || category === 'Done' || category === 'Cancelled') return false;
  const todayIso = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`;
  return dueDate < todayIso;
}
