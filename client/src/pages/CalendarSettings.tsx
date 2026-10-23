import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api } from '../api/client';

import type { AddFeedRequest, CalendarView as Calendar, EventView as CalendarEvent } from '../protocol/generated';

const calendarsApi = {
  list: () => api<Calendar[]>('/api/calendars'),
  add: (name: string, url: string) => api<Calendar>('/api/calendars', { body: { name, url } satisfies AddFeedRequest }),
  sync: (id: string) => api<Calendar>(`/api/calendars/${id}/sync`, { method: 'POST' }),
  remove: (id: string) => api<void>(`/api/calendars/${id}`, { method: 'DELETE' }),
  events: (days = 7) => api<CalendarEvent[]>(`/api/calendars/events?days=${days}`),
  removeEvent: (id: string) => api<void>(`/api/calendars/events/${id}`, { method: 'DELETE' }),
};

const timeFormat = new Intl.DateTimeFormat(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' });
const dayFormat = new Intl.DateTimeFormat(undefined, { weekday: 'short', day: 'numeric', month: 'short', timeZone: 'UTC' });
const syncFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });

/** All-day events are stored as UTC midnight of their date, so they're shown in UTC to keep the date. */
function when(e: CalendarEvent): string {
  return e.allDay ? `${dayFormat.format(new Date(e.startsAt))}, all day` : timeFormat.format(new Date(e.startsAt));
}

/**
 * The calendars the familiar can see (PLAN.md §6.4): its own, which it can write to, and subscribed
 * feeds - the "secret iCal address" Google, Outlook and iCloud all offer - which it only reads.
 */
export function CalendarSettings() {
  const [calendars, setCalendars] = useState<Calendar[] | null>(null);
  const [events, setEvents] = useState<CalendarEvent[]>([]);
  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    calendarsApi.list().then(setCalendars).catch((e: unknown) => setError(e instanceof Error ? e.message : 'Couldn’t load calendars.'));
    calendarsApi.events().then(setEvents).catch(() => undefined);
  }, []);
  useEffect(load, [load]);

  async function add(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const added = await calendarsApi.add(name.trim(), url.trim());
      if (added.lastError) setError(added.lastError);
      setName('');
      setUrl('');
      load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Couldn’t add that calendar.');
    } finally {
      setBusy(false);
    }
  }

  async function act(action: Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await action;
    } catch (e) {
      setError(e instanceof Error ? e.message : 'That didn’t work.');
    } finally {
      setBusy(false);
      load();
    }
  }

  if (!calendars) return error ? <p className="error">{error}</p> : <p className="muted">Loading…</p>;

  const names = new Map(calendars.map((c) => [c.id, c]));

  return (
    <div className="stack">
      <ul className="device-list">
        {calendars.map((c) => (
          <li key={c.id}>
            <div>
              <strong>{c.kind === 'local' ? 'Familiar' : c.kind === 'device' ? `${c.name}’s calendars` : c.name}</strong>
              <span className={c.lastError ? 'error' : 'muted'}>
                {c.kind === 'local'
                  ? 'Its own calendar: new events go here when your phone isn’t connected.'
                  : c.kind === 'device'
                    ? `Every calendar on the phone, from the Familiar app · updated ${c.lastSyncedAt ? syncFormat.format(new Date(c.lastSyncedAt)) : 'never'}. New events go here while the app is open.`
                    : c.lastError ?? (c.lastSyncedAt ? `Read-only feed · updated ${syncFormat.format(new Date(c.lastSyncedAt))}` : 'Not fetched yet')}
              </span>
            </div>
            {c.kind === 'ics' && (
              <span className="row-actions">
                <button type="button" className="link" disabled={busy} onClick={() => void act(calendarsApi.sync(c.id))}>Refresh</button>
                <button type="button" className="secondary" disabled={busy} onClick={() => void act(calendarsApi.remove(c.id))}>Remove</button>
              </span>
            )}
          </li>
        ))}
      </ul>

      <form className="stack" onSubmit={add}>
        <p className="muted">
          Add a calendar by its secret iCal address: Google Calendar → Settings → your calendar → “Secret address in iCal format”;
          Outlook → Settings → Shared calendars → Publish; iCloud → Share → Public calendar.
        </p>
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} maxLength={64} placeholder="Work" required />
        </label>
        <label>
          iCal address
          <input value={url} onChange={(e) => setUrl(e.target.value)} type="url" placeholder="https://… or webcal://…" required />
        </label>
        <button type="submit" disabled={busy || !name.trim() || !url.trim()} aria-busy={busy}>{busy ? 'Fetching…' : 'Add calendar'}</button>
      </form>
      {error && <p className="error" role="alert">{error}</p>}

      <h3>Next 7 days</h3>
      {events.length === 0
        ? <p className="muted">Nothing on the calendar.</p>
        : (
          <ul className="event-list">
            {events.map((e) => (
              <li key={e.id}>
                <span className="event-when muted">{when(e)}</span>
                <span>
                  {e.title}
                  {e.location && <span className="muted"> · {e.location}</span>}
                  {names.get(e.calendarId)?.kind !== 'local' && <span className="muted"> · {names.get(e.calendarId)?.name}</span>}
                </span>
                {names.get(e.calendarId)?.kind === 'local' && (
                  <button type="button" className="link" onClick={() => void act(calendarsApi.removeEvent(e.id))} aria-label={`Delete ${e.title}`}>✕</button>
                )}
              </li>
            ))}
          </ul>
        )}
    </div>
  );
}
