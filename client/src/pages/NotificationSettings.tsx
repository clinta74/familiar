import { useEffect, useState } from 'react';
import { disablePush, enablePush, pushStatus, type PushStatus } from '../notifications/push';

const explanations: Record<PushStatus, string> = {
  on: 'This browser shows timers and reminders even when Familiar’s tab is closed.',
  off: 'Timers and reminders only reach you while Familiar is open in this browser.',
  denied: 'This browser has blocked notifications from Familiar. Allow them in the site settings (the icon left of the address), then come back.',
  insecure: 'Notifications need a secure connection: open Familiar over https with a certificate this browser trusts.',
  unsupported: 'This browser can’t show notifications from websites.',
};

/**
 * Per browser, not per account: each browser subscribes for itself. The notifications carry nothing
 * but "something happened"; this browser then asks Familiar what.
 */
export function NotificationSettings() {
  const [status, setStatus] = useState<PushStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void pushStatus().then(setStatus);
  }, []);

  async function toggle() {
    setBusy(true);
    setError(null);
    try {
      setStatus(status === 'on' ? await disablePush() : await enablePush());
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Couldn’t change notifications.');
    } finally {
      setBusy(false);
    }
  }

  if (!status) return <p className="muted">Checking…</p>;

  return (
    <div className="stack">
      <p className="muted">{explanations[status]}</p>
      {(status === 'on' || status === 'off') && (
        <button type="button" className={status === 'on' ? 'secondary' : undefined} onClick={() => void toggle()} disabled={busy}>
          {status === 'on' ? 'Turn off notifications' : 'Turn on notifications'}
        </button>
      )}
      {error && <p className="error" role="alert">{error}</p>}
    </div>
  );
}
