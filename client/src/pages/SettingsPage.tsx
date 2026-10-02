import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { authApi, type Device } from '../api/auth';
import { useAuth } from '../auth/AuthContext';
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
      <button type="submit" disabled={busy}>Change password</button>
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

export function SettingsPage() {
  const { account } = useAuth();

  return (
    <Shell>
      <div className="settings">
        <section className="card">
          <h2>Account</h2>
          <dl className="facts">
            <dt>Username</dt><dd>{account?.username}</dd>
            <dt>Email</dt><dd>{account?.email}</dd>
            <dt>Role</dt><dd>{account?.role}</dd>
          </dl>
        </section>
        <section className="card">
          <h2>Password</h2>
          <PasswordForm />
        </section>
        <section className="card">
          <h2>Devices</h2>
          <DeviceList />
        </section>
      </div>
    </Shell>
  );
}
