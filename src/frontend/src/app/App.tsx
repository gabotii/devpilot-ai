import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Link, Route, Routes } from 'react-router';
import { ApiError } from '../api/client';
import { AppRoutes } from './routes';
import { ToastProvider } from './providers';

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 15_000,
        refetchOnWindowFocus: true,
        // Never retry client errors (401/403/404/422...): they will not fix themselves.
        retry: (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2,
      },
    },
  });
}

const queryClient = createQueryClient();

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <BrowserRouter>
          <AppRoutes />
        </BrowserRouter>
      </ToastProvider>
    </QueryClientProvider>
  );
}

export function NotFoundPage() {
  return (
    <div className="auth-layout">
      <div className="center-state">
        <h1>Página no encontrada</h1>
        <p>La dirección no existe o ya no está disponible.</p>
        <Link className="btn" to="/">
          Ir al inicio
        </Link>
      </div>
    </div>
  );
}

export { Route, Routes };
