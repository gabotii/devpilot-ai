import { useState } from 'react';
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Rocket } from 'lucide-react';
import { ApiError } from '../api/client';
import { useLogin, useMe, useRegister } from '../api/hooks';
import { Button, Field, InlineError, Input, LoadingState } from '../components/ui';
import { applyServerErrors, safeNext } from '../lib/forms';

function AuthLayout({ title, subtitle, children }: { title: string; subtitle: string; children: React.ReactNode }) {
  return (
    <div className="auth-layout">
      <main className="auth-card">
        <div className="brand">
          <span className="brand-mark" aria-hidden>
            <Rocket size={15} />
          </span>
          DevPilot AI
        </div>
        <div className="card card-body stack">
          <div>
            <h1>{title}</h1>
            <p className="muted">{subtitle}</p>
          </div>
          {children}
        </div>
      </main>
    </div>
  );
}

const loginSchema = z.object({
  email: z.string().trim().min(1, 'Introduce tu email.').email('Email no válido.'),
  password: z.string().min(1, 'Introduce tu contraseña.'),
});
type LoginValues = z.infer<typeof loginSchema>;

export function LoginPage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const me = useMe();
  const login = useLogin();
  const next = safeNext(params.get('next'));
  const form = useForm<LoginValues>({ resolver: zodResolver(loginSchema), defaultValues: { email: '', password: '' } });

  if (me.data) return <Navigate to={next} replace />;
  if (me.isPending) return <LoadingState />;

  const onSubmit = form.handleSubmit(async (values) => {
    try {
      await login.mutateAsync(values);
      navigate(next, { replace: true });
    } catch {
      /* rendered below */
    }
  });

  const errors = form.formState.errors;
  return (
    <AuthLayout title="Iniciar sesión" subtitle="Accede a tus organizaciones y proyectos.">
      <form className="stack" onSubmit={onSubmit} noValidate>
        <Field label="Email" error={errors.email?.message}>
          {(p) => <Input {...p} type="email" autoComplete="email" autoFocus {...form.register('email')} />}
        </Field>
        <Field label="Contraseña" error={errors.password?.message}>
          {(p) => <Input {...p} type="password" autoComplete="current-password" {...form.register('password')} />}
        </Field>
        <InlineError error={login.error} />
        <Button type="submit" variant="primary" loading={form.formState.isSubmitting}>
          Entrar
        </Button>
      </form>
      <p className="muted small">
        ¿No tienes cuenta? <Link to={`/register${params.get('next') ? `?next=${encodeURIComponent(next)}` : ''}`}>Crear cuenta</Link>
      </p>
    </AuthLayout>
  );
}

const registerSchema = z
  .object({
    displayName: z.string().trim().min(1, 'Introduce tu nombre.').max(100),
    email: z.string().trim().min(1, 'Introduce tu email.').email('Email no válido.').max(254),
    password: z.string().min(12, 'Mínimo 12 caracteres.').max(128, 'Máximo 128 caracteres.'),
    confirm: z.string(),
  })
  .refine((v) => v.password === v.confirm, { path: ['confirm'], message: 'Las contraseñas no coinciden.' })
  .refine((v) => v.password.toLowerCase() !== v.email.toLowerCase(), { path: ['password'], message: 'No uses tu email como contraseña.' });
type RegisterValues = z.infer<typeof registerSchema>;

export function RegisterPage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const register = useRegister();
  const [generalError, setGeneralError] = useState<unknown>(null);
  const form = useForm<RegisterValues>({
    resolver: zodResolver(registerSchema),
    defaultValues: { displayName: '', email: '', password: '', confirm: '' },
  });

  const onSubmit = form.handleSubmit(async ({ confirm: _confirm, ...values }) => {
    setGeneralError(null);
    try {
      await register.mutateAsync(values);
      navigate(params.get('next') ? safeNext(params.get('next')) : '/onboarding', { replace: true });
    } catch (error) {
      if (error instanceof ApiError && error.code === 'auth.email_taken') {
        form.setError('email', { message: 'Ya existe una cuenta con este email.' });
      } else if (!applyServerErrors(error, form.setError, ['displayName', 'email', 'password'])) {
        setGeneralError(error);
      }
    }
  });

  const errors = form.formState.errors;
  return (
    <AuthLayout title="Crear cuenta" subtitle="Empieza gratis. Podrás crear tu organización después.">
      <form className="stack" onSubmit={onSubmit} noValidate>
        <Field label="Nombre" error={errors.displayName?.message}>
          {(p) => <Input {...p} autoComplete="name" autoFocus {...form.register('displayName')} />}
        </Field>
        <Field label="Email" error={errors.email?.message}>
          {(p) => <Input {...p} type="email" autoComplete="email" {...form.register('email')} />}
        </Field>
        <Field label="Contraseña" error={errors.password?.message} hint="Al menos 12 caracteres. Una frase larga es más segura que símbolos.">
          {(p) => <Input {...p} type="password" autoComplete="new-password" {...form.register('password')} />}
        </Field>
        <Field label="Repite la contraseña" error={errors.confirm?.message}>
          {(p) => <Input {...p} type="password" autoComplete="new-password" {...form.register('confirm')} />}
        </Field>
        <InlineError error={generalError} />
        <Button type="submit" variant="primary" loading={form.formState.isSubmitting}>
          Crear cuenta
        </Button>
      </form>
      <p className="muted small">
        ¿Ya tienes cuenta? <Link to="/login">Inicia sesión</Link>
      </p>
    </AuthLayout>
  );
}
