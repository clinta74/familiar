import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { Link, Navigate, useLocation, useParams } from 'react-router';
import { authApi, type Device } from '../api/auth';
import { useAuth } from '../auth/AuthContext';
import { FamiliarSettings } from './FamiliarSettings';
import { CalendarSettings } from './CalendarSettings';
import { ConnectionsSettings } from './ConnectionsSettings';
import { MemorySettings } from './MemorySettings';
import { NotificationSettings } from './NotificationSettings';
import { Shell } from './Shell';

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });

function PasswordForm() {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [status, setStatus] = useState<{ ok: boolean; message: string } | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setStatus(null);
    try {
      await authApi.changePassword(current, next);
      setCurrent('');
      setNext('');
      setStatus({ ok: true, message: 'Password changed. Your other sessions and devices have been signed out.' });
    } catch (e) {
      setStatus({ ok: false, message: e instanceof Error ? e.message : 'Could not change the password.' });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="stack">
      <label>
        Current password
        <input type="password" value={current} onChange={(e) => setCurrent(e.target.value)} autoComplete="current-password" required />
      </label>
      <label>
        New password
        <input
          type="password"
          value={next}
          onChange={(e) => setNext(e.target.value)}
          autoComplete="new-password"
          minLength={8}
          maxLength={256}
          required
        />
      </label>
      {status && <p className={status.ok ? 'success' : 'error'} role="status">{status.message}</p>}
      <button type="submit" disabled={busy} aria-busy={busy}>Change password</button>
    </form>
  );
}

function DeviceList() {
  const [devices, setDevices] = useState<Device[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    authApi
      .devices()
      .then(setDevices)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Could not load devices.'));
  }, []);

  useEffect(load, [load]);

  async function signOut(id: string) {
    await authApi.signOutDevice(id);
    load();
  }

  if (error) return <p className="error">{error}</p>;
  if (!devices) return <p className="muted">Loading…</p>;
  if (devices.length === 0) return <p className="muted">No phones are signed in. The Android and iOS apps will appear here.</p>;

  return (
    <ul className="device-list">
      {devices.map((d) => (
        <li key={d.id}>
          <div>
            <strong>{d.name}</strong>
            <span className="muted">
              Signed in {dateFormat.format(new Date(d.createdAt))}
              {d.lastUsedAt && ` · last used ${dateFormat.format(new Date(d.lastUsedAt))}`}
            </span>
          </div>
          <button type="button" className="secondary" onClick={() => void signOut(d.id)}>Sign out</button>
        </li>
      ))}
    </ul>
  );
}

function AccountSettings() {
  const { account } = useAuth();
  return (
    <div className="stack">
      <dl className="facts">
        <dt>Username</dt><dd>{account?.username}</dd>
        <dt>Email</dt><dd>{account?.email}</dd>
        <dt>Role</dt><dd>{account?.role === 'admin' ? 'Administrator' : 'Member'}</dd>
      </dl>
      <h3>Password</h3>
      <PasswordForm />
    </div>
  );
}

/** One page of Settings: its address, its name and line in the list, and what it shows - or where it lives instead. */
type Section = { id: string; title: string; about: string } & ({ render: () => ReactNode } | { to: string });

/** The same pages, in the same order, as the app's Settings (mobile SettingsScreen's Route list), plus this browser's notifications. */
const sections: Section[] = [
  { id: 'familiar', title: 'Your familiar', about: 'Its name, personality, voice, and where you live', render: () => <FamiliarSettings /> },
  { id: 'studio', title: 'Studio', about: 'How it looks', to: '/studio' },
  { id: 'memories', title: 'What it remembers', about: 'Things it has picked up about you', render: () => <MemorySettings /> },
  { id: 'calendars', title: 'Calendars', about: 'The calendars it can see', render: () => <CalendarSettings /> },
  { id: 'connections', title: 'Connections', about: 'Home Assistant, webhooks and MCP tools', render: () => <ConnectionsSettings /> },
  { id: 'notifications', title: 'Notifications', about: 'Timers and reminders when this tab is closed', render: () => <NotificationSettings /> },
  { id: 'account', title: 'Account', about: 'Your details and password', render: () => <AccountSettings /> },
  { id: 'devices', title: 'Devices', about: 'The phones signed in to your account', render: () => <DeviceList /> },
];

/**
 * Settings as a list of pages, as in the app: one short page each, rather than everything at once.
 * /settings is the list; /settings/familiar and so on are the pages.
 */
export function SettingsPage() {
  const { account } = useAuth();
  const { section } = useParams();
  const { hash } = useLocation();

  // Old links to a part of the one long page (/settings#devices) go to its page.
  if (!section && hash && sections.some((s) => s.id === hash.slice(1))) return <Navigate to={`/settings/${hash.slice(1)}`} replace />;

  const page = sections.find((s) => s.id === section);
  if (section && (!page || !('render' in page))) return <Navigate to={page && 'to' in page ? page.to : '/settings'} replace />;

  return (
    <Shell>
      <div className="settings">
        {page && 'render' in page ? (
          <>
            <Link to="/settings" className="settings-back">‹ Settings</Link>
            <section className="card" aria-labelledby="settings-title">
              <h2 id="settings-title">{page.title}</h2>
              {page.render()}
            </section>
          </>
        ) : (
          <>
            <h1 className="settings-heading">Settings</h1>
            <nav className="card settings-nav" aria-label="Settings">
              <ul>
                {sections.map((s) => (
                  <li key={s.id}>
                    <Link to={'to' in s ? s.to : `/settings/${s.id}`}>
                      <span>
                        <strong>{s.title}</strong>
                        <span className="muted small">{s.about}</span>
                      </span>
                      <span className="chevron" aria-hidden="true">›</span>
                    </Link>
                  </li>
                ))}
                {account?.role === 'admin' && (
                  <li>
                    <Link to="/admin">
                      <span>
                        <strong>Users</strong>
                        <span className="muted small">Accounts on this server (administrators only)</span>
                      </span>
                      <span className="chevron" aria-hidden="true">›</span>
                    </Link>
                  </li>
                )}
              </ul>
            </nav>
          </>
        )}
      </div>
    </Shell>
  );
}
