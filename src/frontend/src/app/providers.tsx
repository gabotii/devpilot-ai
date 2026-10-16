import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Navigate, Outlet, useLocation, useNavigate, useParams } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { CheckCircle2, AlertTriangle, Info, X } from 'lucide-react';
import { ApiError, onUnauthorized } from '../api/client';
import { keys, useMe } from '../api/hooks';
import type { Me, OrgRole, OrgSummary } from '../api/types';
import { ORG_ROLE_RANK } from '../api/types';
import { ErrorState, LoadingState } from '../components/ui';

// ---------- Toasts ----------

type ToastTone = 'success' | 'error' | 'info';
interface Toast {
  id: number;
  tone: ToastTone;
  message: string;
}

const ToastContext = createContext<(message: string, tone?: ToastTone) => void>(() => undefined);

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const dismiss = useCallback((id: number) => setToasts((t) => t.filter((x) => x.id !== id)), []);
  const push = useCallback(
    (message: string, tone: ToastTone = 'success') => {
      const id = Date.now() + Math.random();
      setToasts((t) => [...t.slice(-3), { id, tone, message }]);
      window.setTimeout(() => dismiss(id), tone === 'error' ? 8000 : 4000);
    },
    [dismiss],
  );
  return (
    <ToastContext.Provider value={push}>
      {children}
      <div className="toast-region" aria-live="polite" aria-atomic="false">
        {toasts.map((t) => (
          <div key={t.id} className={`toast toast-${t.tone}`} role={t.tone === 'error' ? 'alert' : 'status'}>
            {t.tone === 'success' ? <CheckCircle2 size={18} aria-hidden /> : t.tone === 'error' ? <AlertTriangle size={18} aria-hidden /> : <Info size={18} aria-hidden />}
            <span style={{ flex: 1 }}>{t.message}</span>
            <button className="btn btn-ghost btn-sm btn-icon" onClick={() => dismiss(t.id)} aria-label="Descartar">
              <X size={14} />
            </button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

export const useToast = () => useContext(ToastContext);

// ---------- Auth ----------

const AuthContext = createContext<Me | null>(null);

export function useAuth(): Me {
  const me = useContext(AuthContext);
  if (!me) throw new Error('useAuth must be used inside RequireAuth');
  return me;
}

/** Gate for authenticated routes. Any 401 from the API (expired/revoked session) sends the user to login. */
export function RequireAuth() {
  const me = useMe();
  const location = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  useEffect(
    () =>
      onUnauthorized(() => {
        queryClient.clear();
        navigate(`/login?next=${encodeURIComponent(location.pathname + location.search)}`, { replace: true });
      }),
    [navigate, location.pathname, location.search, queryClient],
  );

  if (me.isPending) return <LoadingState label="Comprobando sesión…" />;
  if (me.error instanceof ApiError && me.error.status === 401) {
    return <Navigate to={`/login?next=${encodeURIComponent(location.pathname + location.search)}`} replace />;
  }
  if (me.error) return <ErrorState error={me.error} onRetry={() => void me.refetch()} />;

  return (
    <AuthContext.Provider value={me.data}>
      <Outlet />
    </AuthContext.Provider>
  );
}

// ---------- Organization context ----------

interface OrgContextValue {
  orgId: string;
  org: OrgSummary;
  role: OrgRole;
  /** UI hint only: the API enforces every permission independently. */
  hasRole: (minimum: OrgRole) => boolean;
  path: (suffix?: string) => string;
}

const OrgContext = createContext<OrgContextValue | null>(null);
const LAST_ORG_KEY = 'devpilot.lastOrg';

export function useOrg(): OrgContextValue {
  const ctx = useContext(OrgContext);
  if (!ctx) throw new Error('useOrg must be used inside an organization route');
  return ctx;
}

export function rememberOrg(orgId: string) {
  try {
    localStorage.setItem(LAST_ORG_KEY, orgId);
  } catch {
    /* storage may be unavailable (private mode); not critical */
  }
}

function lastOrg(): string | null {
  try {
    return localStorage.getItem(LAST_ORG_KEY);
  } catch {
    return null;
  }
}

export function OrgScope({ children }: { children: ReactNode }) {
  const { orgId = '' } = useParams();
  const me = useAuth();
  const org = me.organizations.find((o) => o.id === orgId);

  useEffect(() => {
    if (org) rememberOrg(org.id);
  }, [org]);

  const value = useMemo<OrgContextValue | null>(
    () =>
      org
        ? {
            orgId: org.id,
            org,
            role: org.role,
            hasRole: (minimum) => ORG_ROLE_RANK[org.role] >= ORG_ROLE_RANK[minimum],
            path: (suffix = '') => `/o/${org.id}${suffix}`,
          }
        : null,
    [org],
  );

  if (!value) return <Navigate to="/" replace />;
  return <OrgContext.Provider value={value}>{children}</OrgContext.Provider>;
}

/** "/" → last used organization, first organization, or onboarding when the user has none. */
export function HomeRedirect() {
  const me = useAuth();
  if (me.organizations.length === 0) return <Navigate to="/onboarding" replace />;
  const remembered = lastOrg();
  const target = me.organizations.find((o) => o.id === remembered) ?? me.organizations[0];
  return <Navigate to={target ? `/o/${target.id}` : '/onboarding'} replace />;
}

export function useInvalidateMe() {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: keys.me });
}
