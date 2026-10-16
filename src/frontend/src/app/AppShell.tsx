import { useEffect, useRef, useState } from 'react';
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router';
import {
  Activity,
  Bell,
  Bot,
  Building2,
  ChevronDown,
  FolderKanban,
  LayoutDashboard,
  LogOut,
  Menu,
  Plug,
  Rocket,
  Settings,
  Siren,
  User,
} from 'lucide-react';
import { useLogout, useNotifications } from '../api/hooks';
import { Avatar } from '../components/ui';
import { roleLabel } from '../lib/format';
import { useAuth, useOrg } from './providers';

function OrgSwitcher() {
  const me = useAuth();
  const { org } = useOrg();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useDismiss(ref, open, () => setOpen(false));

  return (
    <div className="dropdown" ref={ref}>
      <button className="btn btn-ghost" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        <Building2 size={16} aria-hidden />
        <span className="truncate" style={{ maxWidth: 200 }}>
          {org.name}
        </span>
        <span className="badge">{roleLabel[org.role]}</span>
        <ChevronDown size={14} aria-hidden />
      </button>
      {open ? (
        <div className="dropdown-menu" role="menu" style={{ left: 0, right: 'auto' }}>
          <div className="muted small" style={{ padding: '4px 10px' }}>
            Organizaciones
          </div>
          {me.organizations.map((o) => (
            <Link key={o.id} role="menuitem" className="dropdown-item" to={`/o/${o.id}`} onClick={() => setOpen(false)}>
              <span className="truncate" style={{ flex: 1 }}>
                {o.name}
              </span>
              {o.id === org.id ? <span className="badge badge-accent">Actual</span> : null}
            </Link>
          ))}
          <div className="dropdown-sep" />
          <Link role="menuitem" className="dropdown-item" to="/onboarding" onClick={() => setOpen(false)}>
            Crear o unirse a una organización
          </Link>
        </div>
      ) : null}
    </div>
  );
}

function UserMenu() {
  const me = useAuth();
  const logout = useLogout();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useDismiss(ref, open, () => setOpen(false));

  return (
    <div className="dropdown" ref={ref}>
      <button className="btn btn-ghost" aria-haspopup="menu" aria-expanded={open} aria-label="Menú de usuario" onClick={() => setOpen((o) => !o)}>
        <Avatar name={me.user.displayName} />
        <span className="truncate" style={{ maxWidth: 140 }}>
          {me.user.displayName}
        </span>
      </button>
      {open ? (
        <div className="dropdown-menu" role="menu">
          <div style={{ padding: '4px 10px' }}>
            <div style={{ fontWeight: 600 }}>{me.user.displayName}</div>
            <div className="muted small truncate">{me.user.email}</div>
          </div>
          <div className="dropdown-sep" />
          <Link role="menuitem" className="dropdown-item" to="/profile" onClick={() => setOpen(false)}>
            <User size={16} aria-hidden /> Perfil y seguridad
          </Link>
          <button
            role="menuitem"
            className="dropdown-item"
            onClick={() =>
              logout.mutate(undefined, {
                onSettled: () => navigate('/login', { replace: true }),
              })
            }
          >
            <LogOut size={16} aria-hidden /> Cerrar sesión
          </button>
        </div>
      ) : null}
    </div>
  );
}

function NotificationBell() {
  const { orgId, path } = useOrg();
  const { data } = useNotifications(orgId, true, 1, 1);
  const unread = data?.unreadCount ?? 0;
  return (
    <Link className="btn btn-ghost btn-icon" to={path('/notifications')} aria-label={`Notificaciones${unread ? `, ${unread} sin leer` : ''}`} style={{ position: 'relative' }}>
      <Bell size={18} aria-hidden />
      {unread > 0 ? (
        <span className="nav-badge" style={{ position: 'absolute', top: 2, right: 0 }} aria-hidden>
          {unread > 99 ? '99+' : unread}
        </span>
      ) : null}
    </Link>
  );
}

const NAV = [
  { section: 'Trabajo' },
  { to: '', label: 'Dashboard', icon: LayoutDashboard, end: true },
  { to: '/projects', label: 'Proyectos', icon: FolderKanban },
  { to: '/incidents', label: 'Incidencias', icon: Siren },
  { section: 'Operaciones' },
  { to: '/deployments', label: 'Despliegues y pipelines', icon: Rocket },
  { to: '/integrations', label: 'Integraciones', icon: Plug },
  { to: '/assistant', label: 'Asistente IA', icon: Bot },
  { section: 'Organización' },
  { to: '/activity', label: 'Actividad', icon: Activity },
  { to: '/notifications', label: 'Notificaciones', icon: Bell },
  { to: '/settings', label: 'Configuración', icon: Settings },
] as const;

export function AppShell() {
  const { path } = useOrg();
  const location = useLocation();
  // The mobile menu belongs to the page it was opened on: navigating anywhere closes it.
  const [menuPath, setMenuPath] = useState<string | null>(null);
  const menuOpen = menuPath === location.pathname;
  const setMenuOpen = (open: boolean) => setMenuPath(open ? location.pathname : null);

  return (
    <div className="shell">
      <a className="skip-link" href="#main">
        Saltar al contenido
      </a>
      <aside className={`sidebar ${menuOpen ? 'open' : ''}`} aria-label="Navegación principal">
        <Link to={path()} className="sidebar-brand">
          <span className="brand-mark" aria-hidden>
            <Rocket size={15} />
          </span>
          DevPilot AI
        </Link>
        <nav>
          {NAV.map((item, i) =>
            'section' in item ? (
              <div key={i} className="nav-section">
                {item.section}
              </div>
            ) : (
              <NavLink key={item.to} to={path(item.to)} end={'end' in item} className={({ isActive }) => `nav-link ${isActive ? 'active' : ''}`}>
                <item.icon size={17} aria-hidden />
                {item.label}
              </NavLink>
            ),
          )}
        </nav>
      </aside>
      <header className="topbar">
        <button className="btn btn-ghost btn-icon menu-toggle" aria-label="Abrir menú" aria-expanded={menuOpen} onClick={() => setMenuOpen(!menuOpen)}>
          <Menu size={18} />
        </button>
        <OrgSwitcher />
        <div className="topbar-spacer" />
        <NotificationBell />
        <UserMenu />
      </header>
      <main id="main" className="main" tabIndex={-1}>
        <Outlet />
      </main>
    </div>
  );
}

/** Closes a popover on outside click or Escape. */
function useDismiss(ref: React.RefObject<HTMLElement | null>, open: boolean, close: () => void) {
  useEffect(() => {
    if (!open) return;
    const onClick = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) close();
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };
    document.addEventListener('mousedown', onClick);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onClick);
      document.removeEventListener('keydown', onKey);
    };
  }, [ref, open, close]);
}
