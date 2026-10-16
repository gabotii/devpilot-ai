import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { LoginPage, RegisterPage } from './AuthPages';
import { makeMe, mockApi, renderRoute } from '../test/utils';

describe('LoginPage', () => {
  it('validates required fields before calling the API', async () => {
    const { calls } = mockApi({ 'GET /api/auth/me': { status: 401, body: {} } });
    renderRoute(<LoginPage />, { path: '/login', route: '/login' });

    await userEvent.click(await screen.findByRole('button', { name: 'Entrar' }));

    expect(await screen.findByText('Introduce tu email.')).toBeInTheDocument();
    expect(screen.getByText('Introduce tu contraseña.')).toBeInTheDocument();
    expect(calls.filter((c) => c.path === '/api/auth/login')).toHaveLength(0);
  });

  it('shows the generic server error on bad credentials', async () => {
    mockApi({
      'GET /api/auth/me': { status: 401, body: {} },
      'POST /api/auth/login': { status: 401, body: { detail: 'Invalid email or password.', code: 'auth.unauthorized' } },
    });
    renderRoute(<LoginPage />, { path: '/login', route: '/login' });

    await userEvent.type(await screen.findByLabelText('Email'), 'ana@example.com');
    await userEvent.type(screen.getByLabelText('Contraseña'), 'wrong-password-1');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Email o contraseña incorrectos.');
  });

  it('logs in and navigates to the requested page', async () => {
    let loggedIn = false;
    const { calls } = mockApi({
      'GET /api/auth/me': () => (loggedIn ? { body: makeMe() } : { status: 401, body: {} }),
      'POST /api/auth/login': () => {
        loggedIn = true;
        return { body: { user: makeMe().user } };
      },
    });
    renderRoute(<LoginPage />, { path: '/login', route: '/login?next=%2Fo%2Fx' });

    await userEvent.type(await screen.findByLabelText('Email'), 'ana@example.com');
    await userEvent.type(screen.getByLabelText('Contraseña'), 'correct-horse-battery-9');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));

    await waitFor(() => expect(screen.getByTestId('navigated')).toBeInTheDocument());
    expect(calls.find((c) => c.path === '/api/auth/login')?.body).toEqual({ email: 'ana@example.com', password: 'correct-horse-battery-9' });
  });
});

describe('RegisterPage', () => {
  it('checks password confirmation and length client-side', async () => {
    mockApi({});
    renderRoute(<RegisterPage />, { path: '/register', route: '/register' });

    await userEvent.type(screen.getByLabelText('Nombre'), 'Ana');
    await userEvent.type(screen.getByLabelText('Email'), 'ana@example.com');
    await userEvent.type(screen.getByLabelText('Contraseña'), 'short');
    await userEvent.type(screen.getByLabelText('Repite la contraseña'), 'different');
    await userEvent.click(screen.getByRole('button', { name: 'Crear cuenta' }));

    expect(await screen.findByText('Mínimo 12 caracteres.')).toBeInTheDocument();
    expect(screen.getByText('Las contraseñas no coinciden.')).toBeInTheDocument();
  });

  it('maps a taken email and server field errors onto the form', async () => {
    mockApi({ 'POST /api/auth/register': { status: 409, body: { detail: 'exists', code: 'auth.email_taken' } } });
    renderRoute(<RegisterPage />, { path: '/register', route: '/register' });

    await userEvent.type(screen.getByLabelText('Nombre'), 'Ana');
    await userEvent.type(screen.getByLabelText('Email'), 'ana@example.com');
    await userEvent.type(screen.getByLabelText('Contraseña'), 'correct-horse-battery-9');
    await userEvent.type(screen.getByLabelText('Repite la contraseña'), 'correct-horse-battery-9');
    await userEvent.click(screen.getByRole('button', { name: 'Crear cuenta' }));

    expect(await screen.findByText('Ya existe una cuenta con este email.')).toBeInTheDocument();
  });
});
