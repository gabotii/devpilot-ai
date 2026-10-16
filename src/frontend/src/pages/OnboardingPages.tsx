import { useEffect, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Building2, MailOpen } from 'lucide-react';
import { errorMessage } from '../api/client';
import { useAcceptInvitation, useCreateOrganization, useMe, useMyInvitations, usePreviewInvitation } from '../api/hooks';
import { useAuth } from '../app/providers';
import { Badge, Button, Card, EmptyState, Field, InlineError, Input, LoadingState } from '../components/ui';
import { formatDate, roleLabel } from '../lib/format';
import { applyServerErrors } from '../lib/forms';

const orgSchema = z.object({ name: z.string().trim().min(2, 'Mínimo 2 caracteres.').max(100) });

export function OnboardingPage() {
  const me = useAuth();
  const navigate = useNavigate();
  const create = useCreateOrganization();
  const invitations = useMyInvitations();
  const accept = useAcceptInvitation();
  const form = useForm<{ name: string }>({ resolver: zodResolver(orgSchema), defaultValues: { name: '' } });

  const onSubmit = form.handleSubmit(async (values) => {
    try {
      const org = await create.mutateAsync(values);
      navigate(`/o/${org.id}`);
    } catch (error) {
      applyServerErrors(error, form.setError, ['name']);
    }
  });

  return (
    <div className="auth-layout">
      <main className="page" style={{ width: '100%', maxWidth: 760 }}>
        <div className="page-header">
          <div>
            <h1>Hola, {me.user.displayName}</h1>
            <p>Crea una organización para tu equipo o únete a una a la que te hayan invitado.</p>
          </div>
          {me.organizations.length > 0 ? (
            <Link className="btn" to="/">
              Volver a mis organizaciones
            </Link>
          ) : null}
        </div>

        <Card title="Invitaciones pendientes" description="Invitaciones dirigidas a tu email.">
          {invitations.isPending ? (
            <LoadingState />
          ) : invitations.data && invitations.data.length > 0 ? (
            <ul className="stack-sm" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
              {invitations.data.map((inv) => (
                <li key={inv.id} className="row-between">
                  <div>
                    <strong>{inv.organizationName}</strong>{' '}
                    <Badge tone="accent">{roleLabel[inv.role]}</Badge>
                    <div className="muted small">Caduca el {formatDate(inv.expiresAt)}</div>
                  </div>
                  <Button
                    variant="primary"
                    size="sm"
                    loading={accept.isPending && accept.variables && 'id' in accept.variables && accept.variables.id === inv.id}
                    onClick={() => accept.mutate({ id: inv.id }, { onSuccess: (org) => navigate(`/o/${org.id}`) })}
                  >
                    Unirme
                  </Button>
                </li>
              ))}
            </ul>
          ) : (
            <EmptyState icon={<MailOpen size={24} aria-hidden />} title="Sin invitaciones" description="Pide a un administrador que te invite con este email." />
          )}
          <InlineError error={accept.error} />
        </Card>

        <Card title="Crear una organización" description="Serás su propietario. Podrás invitar a tu equipo después.">
          <form className="stack" onSubmit={onSubmit} noValidate>
            <Field label="Nombre de la organización" error={form.formState.errors.name?.message}>
              {(p) => <Input {...p} placeholder="Acme Engineering" {...form.register('name')} />}
            </Field>
            <InlineError error={create.error && !form.formState.errors.name ? create.error : null} />
            <div>
              <Button type="submit" variant="primary" loading={form.formState.isSubmitting}>
                <Building2 size={16} aria-hidden /> Crear organización
              </Button>
            </div>
          </form>
        </Card>
      </main>
    </div>
  );
}

const INVITE_TOKEN_KEY = 'devpilot.inviteToken';

function captureInviteToken(hash: string): string {
  const fromHash = hash.replace(/^#/, '');
  try {
    if (fromHash) {
      sessionStorage.setItem(INVITE_TOKEN_KEY, fromHash);
      return fromHash;
    }
    return sessionStorage.getItem(INVITE_TOKEN_KEY) ?? '';
  } catch {
    return fromHash;
  }
}

/**
 * Invitation links look like /invite#<token>. The token travels in the URL fragment so it is never sent to servers,
 * proxies or access logs; it is kept in sessionStorage across the login redirect and posted in a request body.
 */
export function AcceptInvitationPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const [token] = useState(() => captureInviteToken(location.hash));
  const me = useMe();
  const preview = usePreviewInvitation(me.data && token ? token : undefined);
  const accept = useAcceptInvitation();

  useEffect(() => {
    // Remove the token from the address bar/history once captured.
    if (location.hash) window.history.replaceState(null, '', location.pathname);
  }, [location.hash, location.pathname]);

  const onAccept = () =>
    accept.mutate(
      { token },
      {
        onSuccess: (org) => {
          try {
            sessionStorage.removeItem(INVITE_TOKEN_KEY);
          } catch {
            /* ignore */
          }
          navigate(`/o/${org.id}`, { replace: true });
        },
      },
    );

  let content: React.ReactNode;
  if (!token) {
    content = <p className="muted">El enlace de invitación no es válido o ya se usó.</p>;
  } else if (me.isPending) {
    content = <LoadingState />;
  } else if (!me.data) {
    content = (
      <>
        <p>Inicia sesión o crea una cuenta con el email invitado para aceptar la invitación.</p>
        <div className="row">
          <Link className="btn btn-primary" to="/login?next=%2Finvite">
            Iniciar sesión
          </Link>
          <Link className="btn" to="/register?next=%2Finvite">
            Crear cuenta
          </Link>
        </div>
      </>
    );
  } else if (preview.isPending) {
    content = <LoadingState />;
  } else if (preview.error) {
    content = (
      <div className="alert alert-danger" role="alert">
        {errorMessage(preview.error)}
      </div>
    );
  } else if (preview.data) {
    const inv = preview.data;
    content = (
      <>
        <p>
          Te han invitado a <strong>{inv.organizationName}</strong> como <Badge tone="accent">{roleLabel[inv.role]}</Badge>
        </p>
        {inv.status !== 'Pending' ? (
          <div className="alert alert-warning">Esta invitación ya no está disponible ({inv.status.toLowerCase()}).</div>
        ) : !inv.emailMatchesCurrentUser ? (
          <div className="alert alert-warning">
            La invitación es para <strong>{inv.email}</strong>. Inicia sesión con esa cuenta para aceptarla.
          </div>
        ) : (
          <Button variant="primary" loading={accept.isPending} onClick={onAccept}>
            Aceptar y unirme
          </Button>
        )}
        <InlineError error={accept.error} />
      </>
    );
  }

  return (
    <div className="auth-layout">
      <main className="auth-card">
        <div className="card card-body stack">
          <h1>Invitación</h1>
          {content}
          <Link to="/">Ir al inicio</Link>
        </div>
      </main>
    </div>
  );
}
