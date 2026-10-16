import type { ReactElement } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { vi } from 'vitest';
import { ToastProvider } from '../app/providers';
import type { Me, OrgRole } from '../api/types';

type Handler = (body: unknown, url: URL) => { status?: number; body?: unknown } | undefined;

/** Minimal fetch mock: register handlers by "METHOD /path" (query string ignored). Unmatched requests fail loudly. */
export function mockApi(handlers: Record<string, Handler | { status?: number; body?: unknown }>) {
  const calls: { method: string; path: string; body: unknown; headers: Record<string, string> }[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost');
    const method = (init?.method ?? 'GET').toUpperCase();
    const body = init?.body ? JSON.parse(String(init.body)) : undefined;
    calls.push({ method, path: url.pathname, body, headers: (init?.headers ?? {}) as Record<string, string> });
    const handler = handlers[`${method} ${url.pathname}`];
    if (!handler) {
      return new Response(JSON.stringify({ title: 'Unmocked', detail: `${method} ${url.pathname}` }), { status: 599 });
    }
    const result = typeof handler === 'function' ? handler(body, url) : handler;
    const status = result?.status ?? 200;
    return new Response(status === 204 ? null : JSON.stringify(result?.body ?? {}), {
      status,
      headers: { 'Content-Type': 'application/json' },
    });
  });
  vi.stubGlobal('fetch', fetchMock);
  return { fetchMock, calls };
}

export const ORG_ID = '00000000-0000-0000-0000-0000000000aa';

export function makeMe(role: OrgRole = 'Owner', overrides: Partial<Me> = {}): Me {
  return {
    user: { id: 'u1', email: 'ana@example.com', displayName: 'Ana Dev', createdAt: '2026-10-01T00:00:00Z' },
    organizations: [{ id: ORG_ID, name: 'Acme', slug: 'acme', role }],
    pendingInvitations: 0,
    ...overrides,
  };
}

export function renderRoute(ui: ReactElement, { path = '/', route = '/' }: { path?: string; route?: string } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const result = render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <MemoryRouter initialEntries={[route]}>
          <Routes>
            <Route path={path} element={ui} />
            <Route path="*" element={<div data-testid="navigated" />} />
          </Routes>
        </MemoryRouter>
      </ToastProvider>
    </QueryClientProvider>,
  );
  return { ...result, queryClient };
}
