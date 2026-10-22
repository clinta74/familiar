import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { api } from '../api/client';
import type { CreateInviteRequest, InviteView, NewInviteView, RegistrationView } from '../protocol/generated';

const invitesApi = {
  registration: () => api<RegistrationView>('/api/admin/registration'),
  setRegistration: (open: boolean) => api<RegistrationView>('/api/admin/registration', { method: 'PUT', body: { open } satisfies RegistrationView }),
  list: () => api<InviteView[]>('/api/admin/invites'),
  create: (note: string, hours: number) => api<NewInviteView>('/api/admin/invites', { body: { note: note || null, hours } satisfies CreateInviteRequest }),
  cancel: (id: string) => api<void>(`/api/admin/invites/${id}`, { method: 'DELETE' }),
};

const MAX_HOURS = 30 * 24;
const when = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

/**
 * The link to send: the server's, when it knows its public address (Auth:PublicUrl); otherwise this
 * page's own address, which behind a proxy is the one people reach it at.
 */
export function inviteLink(made: NewInviteView, origin = window.location.origin): string {
  return made.link ?? `${origin}/register?invite=${encodeURIComponent(made.code)}`;
}

function describe(invite: InviteView): string {
  switch (invite.status) {
    case 'waiting': return `Works until ${when.format(new Date(invite.expiresAt))}`;
    case 'used': return `Used by ${invite.usedBy ?? 'an account since removed'}${invite.usedAt ? ` on ${when.format(new Date(invite.usedAt))}` : ''}`;
    case 'expired': return `Expired ${when.format(new Date(invite.expiresAt))}`;
    case 'cancelled': return `Cancelled${invite.cancelledAt ? ` ${when.format(new Date(invite.cancelledAt))}` : ''}`;
  }
}

/** The link just made, shown this once - the server keeps only a hash of it - with a way to copy it. */
function NewInvite({ made, onDone }: { made: NewInviteView; onDone: () => void }) {
  const link = inviteLink(made);
  const field = useRef<HTMLInputElement>(null);
  const [copied, setCopied] = useState<boolean | null>(null);

  async function copy() {
    try {
      await navigator.clipboard.writeText(link);
      setCopied(true);
    } catch {
      // The clipboard API needs https (or localhost): on the NAS's plain-http address it isn't
      // there. The old way still works there - copying what's selected in the field.
      field.current?.select();
      setCopied(document.execCommand('copy'));
    }
  }

  return (
    <div className="new-invite stack" role="status">
      <p>
        <strong>Invite made{made.invite.note ? ` for ${made.invite.note}` : ''}.</strong> Send them this link: it lets one
        person register, until {when.format(new Date(made.invite.expiresAt))}. It’s shown only now.
      </p>
      <div className="copy-row">
        <input ref={field} value={link} readOnly aria-label="Invite link" onFocus={(e) => e.target.select()} />
        <button type="button" onClick={() => void copy()}>{copied ? 'Copied' : 'Copy link'}</button>
      </div>
      {copied === false && <p className="error">Couldn’t copy it here: select the link and copy it yourself.</p>}
      <button type="button" className="link" onClick={onDone}>Done</button>
    </div>
  );
}

/** Invite-only registration, for admins: who may register, and the single-use links to let someone in. */
export function InviteAdmin() {
  const [open, setOpen] = useState<boolean | null>(null);
  const [invites, setInvites] = useState<InviteView[] | null>(null);
  const [note, setNote] = useState('');
  const [amount, setAmount] = useState(3);
  const [unit, setUnit] = useState<'hours' | 'days'>('days');
  const [made, setMade] = useState<NewInviteView | null>(null);
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    invitesApi.registration().then((r) => setOpen(r.open)).catch(() => undefined);
    invitesApi.list().then(setInvites).catch((e: unknown) => setStatus({ ok: false, text: message(e, 'Couldn’t load the invites.') }));
  }, []);
  useEffect(load, [load]);

  const hours = unit === 'days' ? amount * 24 : amount;

  async function setRegistration(next: boolean) {
    setStatus(null);
    try {
      setOpen((await invitesApi.setRegistration(next)).open);
    } catch (e) {
      setStatus({ ok: false, text: message(e, 'Couldn’t change that.') });
    }
  }

  async function create(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setStatus(null);
    try {
      const created = await invitesApi.create(note.trim(), hours);
      setMade(created);
      setInvites((list) => [created.invite, ...(list ?? [])]);
      setNote('');
    } catch (e) {
      setStatus({ ok: false, text: message(e, 'Couldn’t make an invite.') });
    } finally {
      setBusy(false);
    }
  }

  async function cancel(invite: InviteView) {
    setStatus(null);
    try {
      await invitesApi.cancel(invite.id);
      // Its link, if it's the one still showing, is no use to anyone now.
      setMade((shown) => (shown?.invite.id === invite.id ? null : shown));
      load();
    } catch (e) {
      setStatus({ ok: false, text: message(e, 'Couldn’t cancel that invite.') });
    }
  }

  return (
    <div className="stack">
      <fieldset className="stack registration-mode">
        <legend>Who can register</legend>
        <label className="check">
          <input type="radio" name="registration" checked={open === true} onChange={() => void setRegistration(true)} disabled={open === null} />
          <span>Anyone <span className="muted">— who can reach this server</span></span>
        </label>
        <label className="check">
          <input type="radio" name="registration" checked={open === false} onChange={() => void setRegistration(false)} disabled={open === null} />
          <span>Only with an invite <span className="muted">— a link you make below, for one person</span></span>
        </label>
      </fieldset>

      {made && <NewInvite made={made} onDone={() => setMade(null)} />}

      <form className="stack" onSubmit={create}>
        <h3>Invite someone</h3>
        <label>
          Who it’s for <span className="muted">(optional, for you)</span>
          <input value={note} onChange={(e) => setNote(e.target.value)} maxLength={80} placeholder="Mum, Sam’s tablet…" />
        </label>
        <div className="ttl-row">
          <label>
            Works for
            <input
              type="number"
              min={1}
              max={unit === 'days' ? 30 : MAX_HOURS}
              value={amount}
              onChange={(e) => setAmount(Math.max(1, Number(e.target.value) || 1))}
              required
            />
          </label>
          <label>
            <span className="visually-hidden">Unit</span>
            <select value={unit} onChange={(e) => setUnit(e.target.value as 'hours' | 'days')}>
              <option value="hours">hours</option>
              <option value="days">days</option>
            </select>
          </label>
        </div>
        {hours > MAX_HOURS && <p className="error">An invite can last 30 days at most.</p>}
        <button type="submit" disabled={busy || hours > MAX_HOURS} aria-busy={busy}>Make invite link</button>
      </form>

      {status && <p className={status.ok ? 'success' : 'error'} role="status">{status.text}</p>}

      <h3>Invites</h3>
      {!invites ? <p className="muted">Loading…</p> : invites.length === 0 ? <p className="muted">None yet.</p> : (
        <ul className="invite-list">
          {invites.map((invite) => (
            <li key={invite.id} className={`invite ${invite.status}`}>
              <div className="invite-who">
                <strong>{invite.note ?? 'Invite'}</strong>
                <span className={`chip ${invite.status}`}>{invite.status}</span>
              </div>
              <span className="muted">
                {describe(invite)} · made {when.format(new Date(invite.createdAt))}{invite.createdBy ? ` by ${invite.createdBy}` : ''}
              </span>
              {invite.status === 'waiting' && (
                <button type="button" className="link danger" onClick={() => void cancel(invite)}>Cancel</button>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
