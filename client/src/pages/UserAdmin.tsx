import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api } from '../api/client';

import type { AdminUserView as AdminUser, CreateUserRequest, ResetPasswordRequest, UpdateUserRequest } from '../protocol/generated';

const usersApi = {
  list: () => api<AdminUser[]>('/api/admin/users'),
  create: (username: string, email: string, password: string, role: AdminUser['role']) =>
    api<AdminUser>('/api/admin/users', { body: { username, email, password, role } satisfies CreateUserRequest }),
  update: (id: string, patch: Partial<UpdateUserRequest>) =>
    api<AdminUser>(`/api/admin/users/${id}`, { method: 'PUT', body: patch }),
  resetPassword: (id: string, password: string) => api<void>(`/api/admin/users/${id}/password`, { body: { password } satisfies ResetPasswordRequest }),
  remove: (id: string) => api<void>(`/api/admin/users/${id}`, { method: 'DELETE' }),
};

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' });
const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

function UserRow({ user, onChanged, onRemoved }: { user: AdminUser; onChanged: (u: AdminUser) => void; onRemoved: () => void }) {
  const [resetting, setResetting] = useState(false);
  const [password, setPassword] = useState('');
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  async function act(action: () => Promise<void>, done?: string) {
    setBusy(true);
    setStatus(null);
    try {
      await action();
      if (done) setStatus({ ok: true, text: done });
    } catch (e) {
      setStatus({ ok: false, text: message(e, 'That didn’t work.') });
    } finally {
      setBusy(false);
    }
  }

  async function reset(event: FormEvent) {
    event.preventDefault();
    await act(async () => {
      await usersApi.resetPassword(user.id, password);
      setResetting(false);
      setPassword('');
    }, `Password reset. ${user.username} has been signed out everywhere; give them the new one.`);
  }

  const remove = () => {
    if (!window.confirm(`Delete ${user.username}? Their familiar, conversations, memories and calendars go too. This can’t be undone.`)) return;
    void act(async () => {
      await usersApi.remove(user.id);
      onRemoved();
    });
  };

  return (
    <li className={`user-row${user.isDisabled ? ' disabled' : ''}`}>
      <div className="user-summary">
        <div>
          <strong>{user.username}</strong>
          {user.role === 'admin' && <span className="badge">admin</span>}
          {user.isDisabled && <span className="badge muted">disabled</span>}
          {user.isYou && <span className="muted small"> (you)</span>}
          <span className="muted small user-meta">
            {user.email} · joined {dateFormat.format(new Date(user.createdAt))}
            {' · '}
            {user.lastLoginAt ? `last signed in ${dateFormat.format(new Date(user.lastLoginAt))}` : 'never signed in'}
            {user.devices > 0 && ` · ${user.devices} phone${user.devices === 1 ? '' : 's'}`}
          </span>
        </div>
        {!user.isYou && (
          <span className="row-actions wrap">
            <button type="button" className="link" disabled={busy} onClick={() => setResetting((r) => !r)}>Reset password</button>
            <button
              type="button"
              className="link"
              disabled={busy}
              onClick={() => void act(async () => onChanged(await usersApi.update(user.id, { role: user.role === 'admin' ? 'user' : 'admin' })))}
            >
              {user.role === 'admin' ? 'Make user' : 'Make admin'}
            </button>
            <button
              type="button"
              className="link"
              disabled={busy}
              onClick={() => void act(async () => onChanged(await usersApi.update(user.id, { isDisabled: !user.isDisabled })))}
            >
              {user.isDisabled ? 'Enable' : 'Disable'}
            </button>
            <button type="button" className="link danger" disabled={busy} onClick={remove}>Delete</button>
          </span>
        )}
      </div>
      {resetting && (
        <form className="memory-edit" onSubmit={reset}>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            placeholder="New password (8+ characters)"
            minLength={8}
            maxLength={256}
            autoComplete="new-password"
            aria-label={`New password for ${user.username}`}
            autoFocus
          />
          <button type="submit" disabled={busy || password.length < 8}>Set</button>
        </form>
      )}
      {status && <p className={status.ok ? 'success small' : 'error small'} role="status">{status.text}</p>}
    </li>
  );
}

/**
 * The server's accounts, for admins (the Users page, /admin). Registration stays closed on the NAS; the
 * household's accounts are added here, each with a first password to pass on.
 */
export function UserAdmin() {
  const [users, setUsers] = useState<AdminUser[] | null>(null);
  const [username, setUsername] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [admin, setAdmin] = useState(false);
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    usersApi.list().then(setUsers).catch((e: unknown) => setStatus({ ok: false, text: message(e, 'Couldn’t load the users.') }));
  }, []);
  useEffect(load, [load]);

  async function add(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setStatus(null);
    try {
      const created = await usersApi.create(username.trim(), email.trim(), password, admin ? 'admin' : 'user');
      setUsers((list) => [...(list ?? []), created]);
      setStatus({ ok: true, text: `Added ${created.username}. Give them their password; they can change it in Settings.` });
      setUsername('');
      setEmail('');
      setPassword('');
      setAdmin(false);
    } catch (e) {
      setStatus({ ok: false, text: message(e, 'Couldn’t add that account.') });
    } finally {
      setBusy(false);
    }
  }

  if (!users) return status ? <p className="error">{status.text}</p> : <p className="muted">Loading…</p>;

  return (
    <div className="stack">
      <ul className="user-list">
        {users.map((u) => (
          <UserRow
            key={u.id}
            user={u}
            onChanged={(changed) => setUsers((list) => list?.map((x) => (x.id === changed.id ? changed : x)) ?? null)}
            onRemoved={() => setUsers((list) => list?.filter((x) => x.id !== u.id) ?? null)}
          />
        ))}
      </ul>

      <form className="stack" onSubmit={add}>
        <h3>Add someone</h3>
        <label>
          Username
          <input value={username} onChange={(e) => setUsername(e.target.value)} pattern="[A-Za-z0-9_]{3,24}" maxLength={24} autoComplete="off" required />
        </label>
        <label>
          Email
          <input value={email} onChange={(e) => setEmail(e.target.value)} type="email" autoComplete="off" required />
        </label>
        <label>
          First password
          <input value={password} onChange={(e) => setPassword(e.target.value)} type="password" minLength={8} maxLength={256} autoComplete="new-password" required />
        </label>
        <label className="check">
          <input type="checkbox" checked={admin} onChange={(e) => setAdmin(e.target.checked)} />
          <span>Admin <span className="muted">— can manage users, like you</span></span>
        </label>
        <button type="submit" disabled={busy}>Add account</button>
      </form>
      {status && <p className={status.ok ? 'success' : 'error'} role="status">{status.text}</p>}
    </div>
  );
}
