import { useEffect, useId, useRef, type ButtonHTMLAttributes, type ComponentProps, type ReactNode } from 'react';
import { AlertTriangle, Inbox, X } from 'lucide-react';
import { ApiError, errorMessage } from '../api/client';

type Variant = 'default' | 'primary' | 'danger' | 'ghost';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: 'md' | 'sm';
  icon?: boolean;
  loading?: boolean;
}

export function Button({ variant = 'default', size = 'md', icon, loading, className, children, disabled, type, ...rest }: ButtonProps) {
  const classes = [
    'btn',
    variant !== 'default' && `btn-${variant}`,
    size === 'sm' && 'btn-sm',
    icon && 'btn-icon',
    className,
  ]
    .filter(Boolean)
    .join(' ');
  return (
    <button type={type ?? 'button'} className={classes} disabled={disabled || loading} aria-busy={loading || undefined} {...rest}>
      {loading ? <span className="spinner" style={{ width: 14, height: 14 }} aria-hidden /> : null}
      {children}
    </button>
  );
}

interface FieldProps {
  label: string;
  error?: string | undefined;
  hint?: string | undefined;
  children: (props: { id: string; 'aria-invalid'?: boolean; 'aria-describedby'?: string }) => ReactNode;
}

/** Accessible form field: wires label, hint and error message to the control. */
export function Field({ label, error, hint, children }: FieldProps) {
  const id = useId();
  const describedBy = [hint && `${id}-hint`, error && `${id}-error`].filter(Boolean).join(' ') || undefined;
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {children({ id, 'aria-invalid': error ? true : undefined, 'aria-describedby': describedBy })}
      {hint && !error ? (
        <span id={`${id}-hint`} className="field-hint">
          {hint}
        </span>
      ) : null}
      {error ? (
        <span id={`${id}-error`} className="field-error" role="alert">
          {error}
        </span>
      ) : null}
    </div>
  );
}

export const Input = (props: ComponentProps<'input'>) => <input {...props} className={`input ${props.className ?? ''}`} />;
export const Select = (props: ComponentProps<'select'>) => <select {...props} className={`select ${props.className ?? ''}`} />;
export const Textarea = (props: ComponentProps<'textarea'>) => (
  <textarea {...props} className={`textarea ${props.className ?? ''}`} />
);

export function Badge({
  tone,
  children,
  title,
}: {
  tone?: 'success' | 'warning' | 'danger' | 'info' | 'accent';
  children: ReactNode;
  title?: string;
}) {
  return (
    <span className={`badge ${tone ? `badge-${tone}` : ''}`} title={title}>
      {children}
    </span>
  );
}

export function Spinner({ label = 'Cargando…' }: { label?: string }) {
  return (
    <span role="status" className="row" style={{ gap: 8 }}>
      <span className="spinner" aria-hidden />
      <span className="sr-only">{label}</span>
    </span>
  );
}

export function LoadingState({ label = 'Cargando…' }: { label?: string }) {
  return (
    <div className="center-state" role="status" aria-live="polite">
      <span className="spinner" aria-hidden />
      <span>{label}</span>
    </div>
  );
}

export function EmptyState({ title, description, action, icon }: { title: string; description?: string; action?: ReactNode; icon?: ReactNode }) {
  return (
    <div className="center-state">
      {icon ?? <Inbox size={28} aria-hidden />}
      <h3>{title}</h3>
      {description ? <p>{description}</p> : null}
      {action}
    </div>
  );
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const notFound = error instanceof ApiError && error.status === 404;
  return (
    <div className="center-state" role="alert">
      <AlertTriangle size={28} aria-hidden />
      <h3>{notFound ? 'No encontrado' : 'No se pudo cargar'}</h3>
      <p>{notFound ? 'El recurso no existe o no tienes acceso.' : errorMessage(error)}</p>
      {error instanceof ApiError && error.traceId ? <p className="small mono">traceId: {error.traceId}</p> : null}
      {onRetry && !notFound ? (
        <Button size="sm" onClick={onRetry}>
          Reintentar
        </Button>
      ) : null}
    </div>
  );
}

export function InlineError({ error }: { error: unknown }) {
  if (!error) return null;
  return (
    <div className="alert alert-danger" role="alert">
      <AlertTriangle size={16} aria-hidden style={{ marginTop: 2 }} />
      <span>{errorMessage(error)}</span>
    </div>
  );
}

export function Card({ title, description, actions, children, bodyClassName }: {
  title?: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  bodyClassName?: string;
}) {
  return (
    <section className="card">
      {title ? (
        <header className="card-header">
          <div>
            <h2>{title}</h2>
            {description ? <p>{description}</p> : null}
          </div>
          {actions}
        </header>
      ) : null}
      <div className={bodyClassName ?? 'card-body'}>{children}</div>
    </section>
  );
}

interface DialogProps {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
}

/** Native <dialog>: focus trapping, Escape to close and inert background are handled by the browser. */
export function Dialog({ open, title, onClose, children, footer, wide }: DialogProps) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) {
      if (typeof dialog.showModal === 'function') dialog.showModal();
      else dialog.setAttribute('open', '');
    } else if (!open && dialog.open) {
      if (typeof dialog.close === 'function') dialog.close();
      else dialog.removeAttribute('open');
    }
  }, [open]);

  return (
    <dialog
      ref={ref}
      className={`dialog ${wide ? 'dialog-wide' : ''}`}
      aria-labelledby={titleId}
      onCancel={(e) => {
        e.preventDefault();
        onClose();
      }}
    >
      {open ? (
        <>
          <div className="dialog-header">
            <h2 id={titleId}>{title}</h2>
            <Button variant="ghost" size="sm" icon onClick={onClose} aria-label="Cerrar">
              <X size={16} />
            </Button>
          </div>
          <div className="dialog-body">{children}</div>
          {footer ? <div className="dialog-footer">{footer}</div> : null}
        </>
      ) : null}
    </dialog>
  );
}

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  message: ReactNode;
  confirmLabel: string;
  destructive?: boolean;
  loading?: boolean;
  error?: unknown;
  onConfirm: () => void;
  onCancel: () => void;
}

export function ConfirmDialog({ open, title, message, confirmLabel, destructive, loading, error, onConfirm, onCancel }: ConfirmDialogProps) {
  return (
    <Dialog
      open={open}
      title={title}
      onClose={onCancel}
      footer={
        <>
          <Button onClick={onCancel}>Cancelar</Button>
          <Button variant={destructive ? 'danger' : 'primary'} loading={loading} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </>
      }
    >
      <div className="stack">
        <div>{message}</div>
        <InlineError error={error} />
      </div>
    </Dialog>
  );
}

export function Pagination({ page, totalPages, totalCount, onPage }: { page: number; totalPages: number; totalCount: number; onPage: (p: number) => void }) {
  if (totalCount === 0) return null;
  return (
    <nav className="pagination" aria-label="Paginación">
      <span className="muted small">
        {totalCount} resultado{totalCount === 1 ? '' : 's'} · página {page} de {Math.max(totalPages, 1)}
      </span>
      <div className="row">
        <Button size="sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>
          Anterior
        </Button>
        <Button size="sm" disabled={page >= totalPages} onClick={() => onPage(page + 1)}>
          Siguiente
        </Button>
      </div>
    </nav>
  );
}

export function Avatar({ name, small }: { name: string; small?: boolean }) {
  const initials = name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((p) => p[0]?.toUpperCase())
    .join('');
  return (
    <span className={`avatar ${small ? 'avatar-sm' : ''}`} aria-hidden title={name}>
      {initials || '?'}
    </span>
  );
}

export function Skeleton({ height = 16, width = '100%' }: { height?: number; width?: number | string }) {
  return <div className="skeleton" style={{ height, width }} aria-hidden />;
}
