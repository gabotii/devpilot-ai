import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import { AppRoutes } from '../app/routes';
import { ToastProvider } from '../app/providers';
import type { OrgRole, ProjectDetail, WorkItemDetail } from '../api/types';
import { makeMe, mockApi, ORG_ID } from '../test/utils';

function renderApp(route: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <MemoryRouter initialEntries={[route]}>
          <AppRoutes />
        </MemoryRouter>
      </ToastProvider>
    </QueryClientProvider>,
  );
}

const emptyPage = { items: [], page: 1, pageSize: 24, totalCount: 0, totalPages: 0 };
const notifications = { page: emptyPage, unreadCount: 0 };
const base = `/api/orgs/${ORG_ID}`;

function shellMocks(role: OrgRole) {
  return {
    'GET /api/auth/me': { body: makeMe(role) },
    [`GET ${base}/notifications`]: { body: notifications },
  };
}

describe('authenticated shell', () => {
  it('redirects anonymous users to login, keeping the target', async () => {
    mockApi({ 'GET /api/auth/me': { status: 401, body: {} } });
    renderApp(`/o/${ORG_ID}/projects`);
    expect(await screen.findByRole('heading', { name: 'Iniciar sesión' })).toBeInTheDocument();
  });

  it('sends users without organizations to onboarding', async () => {
    mockApi({ 'GET /api/auth/me': { body: makeMe('Owner', { organizations: [] }) }, 'GET /api/invitations/mine': { body: [] } });
    renderApp('/');
    expect(await screen.findByRole('heading', { name: /Hola, Ana Dev/ })).toBeInTheDocument();
    expect(await screen.findByText('Sin invitaciones')).toBeInTheDocument();
  });

  it('shows an error state with retry when the API fails', async () => {
    mockApi({ ...shellMocks('Owner'), [`GET ${base}/projects`]: { status: 500, body: { detail: 'boom', traceId: 'abc' } } });
    renderApp(`/o/${ORG_ID}/projects`);
    expect(await screen.findByText('No se pudo cargar')).toBeInTheDocument();
    expect(screen.getByText('traceId: abc')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reintentar' })).toBeInTheDocument();
  });
});

describe('projects permissions in the UI', () => {
  it('viewers do not get the create button', async () => {
    mockApi({ ...shellMocks('Viewer'), [`GET ${base}/projects`]: { body: emptyPage } });
    renderApp(`/o/${ORG_ID}/projects`);
    expect(await screen.findByText('Aún no hay proyectos')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Nuevo proyecto/ })).not.toBeInTheDocument();
  });

  it('developers can create a project and are taken to it', async () => {
    const { calls } = mockApi({
      ...shellMocks('Developer'),
      [`GET ${base}/projects`]: { body: emptyPage },
      [`POST ${base}/projects`]: (body) => ({ status: 201, body: { id: 'p1', ...(body as object) } }),
    });
    renderApp(`/o/${ORG_ID}/projects`);

    await userEvent.click(await screen.findByRole('button', { name: /Nuevo proyecto/ }));
    const dialog = screen.getByRole('dialog');
    await userEvent.type(within(dialog).getByLabelText('Nombre'), 'Payments API');
    expect(within(dialog).getByLabelText('Clave')).toHaveValue('PA');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Crear proyecto' }));

    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === `${base}/projects`)).toBe(true));
    expect(calls.find((c) => c.method === 'POST')?.body).toMatchObject({ name: 'Payments API', key: 'PA' });
  });

  it('shows the key-taken conflict on the key field', async () => {
    mockApi({
      ...shellMocks('Admin'),
      [`GET ${base}/projects`]: { body: emptyPage },
      [`POST ${base}/projects`]: { status: 409, body: { code: 'project.key_taken', detail: 'taken' } },
    });
    renderApp(`/o/${ORG_ID}/projects`);
    await userEvent.click(await screen.findByRole('button', { name: /Nuevo proyecto/ }));
    const dialog = screen.getByRole('dialog');
    await userEvent.type(within(dialog).getByLabelText('Nombre'), 'Payments');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Crear proyecto' }));
    expect(await within(dialog).findByText('Esta clave ya está en uso.')).toBeInTheDocument();
  });
});

const project: ProjectDetail = {
  id: 'p1',
  name: 'Payments',
  key: 'PAY',
  description: null,
  status: 'Active',
  createdAt: '2026-10-01T00:00:00Z',
  updatedAt: '2026-10-01T00:00:00Z',
  archivedAt: null,
  members: [{ userId: 'u1', displayName: 'Ana Dev', email: 'ana@example.com', role: 'Lead', orgRole: 'Owner', addedAt: '2026-10-01T00:00:00Z' }],
  statuses: [
    { id: 's1', name: 'To do', category: 'Todo', position: 0 },
    { id: 's2', name: 'In progress', category: 'InProgress', position: 1 },
    { id: 's3', name: 'Done', category: 'Done', position: 2 },
  ],
  transitions: [
    { fromStatusId: 's1', toStatusId: 's2' },
    { fromStatusId: 's2', toStatusId: 's3' },
  ],
  labels: [],
  permissions: { canEditItems: true, canManage: true, canDelete: true },
  openItems: 1,
  totalItems: 1,
};

const item: WorkItemDetail = {
  item: {
    id: 'w1',
    projectId: 'p1',
    key: 'PAY-1',
    number: 1,
    type: 'Issue',
    title: 'Checkout returns 500',
    statusId: 's1',
    statusName: 'To do',
    statusCategory: 'Todo',
    priority: 'High',
    assignee: null,
    dueDate: null,
    labels: [],
    parentId: null,
    subtaskCount: 0,
    openSubtaskCount: 0,
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
    completedAt: null,
  },
  description: 'Steps…',
  reporter: { id: 'u1', displayName: 'Ana Dev', email: 'ana@example.com' },
  parent: null,
  subtasks: [],
  commentCount: 0,
  version: 7,
  allowedTransitions: [{ id: 's2', name: 'In progress', category: 'InProgress', position: 1 }],
  permissions: { canEditItems: true, canManage: true, canDelete: true },
};

describe('work item detail', () => {
  const itemPath = `${base}/projects/p1/items/1`;
  const mocks = (extra: Record<string, unknown>) => ({
    ...shellMocks('Owner'),
    [`GET ${base}/projects/p1`]: { body: project },
    [`GET ${itemPath}`]: { body: item },
    [`GET ${itemPath}/comments`]: { body: { ...emptyPage, pageSize: 100 } },
    ...extra,
  });

  it('only offers the transitions allowed by the workflow', async () => {
    mockApi(mocks({}));
    renderApp(`/o/${ORG_ID}/projects/p1/items/1`);
    const select = await screen.findByLabelText('Estado');
    const options = within(select).getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual(['To do', '→ In progress']);
  });

  it('sends the version and explains a concurrent edit conflict', async () => {
    const { calls } = mockApi(
      mocks({ [`PUT ${itemPath}`]: { status: 409, body: { code: 'workitem.stale', detail: 'modified' } } }),
    );
    renderApp(`/o/${ORG_ID}/projects/p1/items/1`);
    await userEvent.click(await screen.findByRole('button', { name: /Editar/ }));
    const title = screen.getByLabelText('Título');
    await userEvent.clear(title);
    await userEvent.type(title, 'New title');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }));

    expect(await screen.findByText(/Otra persona modificó esta tarea/)).toBeInTheDocument();
    expect(calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ title: 'New title', version: 7, priority: 'High' });
  });

  it('requires confirmation before deleting', async () => {
    const { calls } = mockApi(mocks({ [`DELETE ${itemPath}`]: { status: 204 } }));
    renderApp(`/o/${ORG_ID}/projects/p1/items/1`);
    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar tarea' }));
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Eliminar definitivamente' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE')).toBe(true));
  });
});
