import { useEffect, useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router';
import { authApi } from '../api/auth';
import { useAuth } from '../auth/AuthContext';
import type { RegistrationStatusView } from '../protocol/generated';
import { AuthCard } from './AuthCard';

const when = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });

/**
 * Registering: open to anyone, or - on a server that's invite only - with the link an admin sent
 * (`/register?invite=…`). The invite is checked before the form shows, so a used or expired one
 * says so up front rather than after the form's been filled in.
 */
export function RegisterPage() {
  const { register } = useAuth();
  const invite = useSearchParams()[0].get('invite');
  const [registration, setRegistration] = useState<RegistrationStatusView | null>(null);
  useEffect(() => {
    // If the check fails, the form shows anyway: registering says what's wrong.
    authApi.registration(invite).then(setRegistration).catch(() => setRegistration({ open: true, invite: null }));
  }, [invite]);
  const [username, setUsername] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await register(username, email, password, invite);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Registration failed.');
      setBusy(false);
    }
  }

  if (!registration) {
    return <AuthCard title="Meet your familiar"><p className="muted">Loading…</p></AuthCard>;
  }

  const check = registration.invite;
  if ((check && !check.valid) || (!invite && !registration.open)) {
    return (
      <AuthCard title="Meet your familiar">
        <p className={check ? 'error' : undefined} role="alert">
          {check?.problem ?? 'Registering on this server needs an invite: ask the person who runs it for a link.'}
        </p>
        <p className="muted">Already have an account? <Link to="/login">Sign in</Link></p>
      </AuthCard>
    );
  }

  return (
    <AuthCard title="Meet your familiar">
      {check?.valid && (
        <p className="muted">
          You’ve been invited. The invite is for one account{check.expiresAt ? `, until ${when.format(new Date(check.expiresAt))}` : ''}.
        </p>
      )}
      <form onSubmit={submit} className="stack">
        <label>
          Username
          <input
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="username"
            pattern="[A-Za-z0-9_]{3,24}"
            title="3-24 letters, digits or underscores"
            autoFocus
            required
          />
        </label>
        <label>
          Email
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="email" required />
        </label>
        <label>
          Password
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="new-password"
            minLength={8}
            maxLength={256}
            required
          />
        </label>
        {error && <p className="error" role="alert">{error}</p>}
        <button type="submit" disabled={busy} aria-busy={busy}>{busy ? 'Creating…' : 'Create account'}</button>
      </form>
      <p className="muted">Already have one? <Link to="/login">Sign in</Link></p>
    </AuthCard>
  );
}
