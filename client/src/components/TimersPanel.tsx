import { useEffect, useState } from 'react';
import type { JobView } from '../conversation/events';
import { enablePush, pushStatus, type PushStatus } from '../notifications/push';

interface Props {
  jobs: JobView[];
  onCancel: (id: string) => void;
}

const timeFormat = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' });
const dayFormat = new Intl.DateTimeFormat(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' });

/** "9:41", "1:02:05": what's left on a timer. */
export function countdown(ms: number): string {
  const total = Math.max(0, Math.ceil(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const pad = (n: number) => String(n).padStart(2, '0');
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}

function when(dueAt: Date, now: Date): string {
  const sameDay = dueAt.toDateString() === now.toDateString();
  return sameDay ? timeFormat.format(dueAt) : dayFormat.format(dueAt);
}

function timerName(job: JobView): string {
  if (job.label) return job.label;
  const minutes = Math.round((job.durationSeconds ?? 0) / 60);
  return minutes >= 1 ? `${minutes} min timer` : 'Timer';
}

/**
 * Running timers (counting down) and upcoming reminders, under the familiar. The familiar sets
 * them; this shows them and lets them be cancelled. Offers notifications the first time it has
 * something to notify about, since that's when the user can see why they'd want them.
 */
export function TimersPanel({ jobs, onCancel }: Props) {
  const [now, setNow] = useState(() => new Date());
  const [push, setPush] = useState<PushStatus | null>(null);
  const hasTimers = jobs.some((j) => j.kind === 'timer');

  useEffect(() => {
    if (!hasTimers) return;
    const tick = window.setInterval(() => setNow(new Date()), 1000);
    return () => window.clearInterval(tick);
  }, [hasTimers]);

  useEffect(() => {
    if (jobs.length > 0 && push === null) void pushStatus().then(setPush);
  }, [jobs.length, push]);

  if (jobs.length === 0) return null;

  return (
    <section className="timers" aria-label="Timers and reminders">
      <ul>
        {jobs.map((job) => {
          const due = new Date(job.dueAt);
          return (
            <li key={job.id} className={`job ${job.kind}`}>
              <span className="job-icon" aria-hidden="true">{job.kind === 'timer' ? '⏱' : '🔔'}</span>
              <span className="job-label">{job.kind === 'timer' ? timerName(job) : job.label}</span>
              <span className="job-when">
                {job.kind === 'timer'
                  ? <time dateTime={job.dueAt} title={`Rings at ${timeFormat.format(due)}`}>{countdown(due.getTime() - now.getTime())}</time>
                  : <time dateTime={job.dueAt}>{when(due, now)}</time>}
              </span>
              <button
                type="button"
                className="link job-cancel"
                onClick={() => onCancel(job.id)}
                aria-label={`Cancel ${job.kind === 'timer' ? timerName(job) : `reminder: ${job.label}`}`}
              >
                ✕
              </button>
            </li>
          );
        })}
      </ul>
      {push === 'off' && (
        <p className="push-nudge muted">
          Want these to reach you with this tab closed?{' '}
          <button type="button" className="link" onClick={() => void enablePush().then(setPush).catch(() => setPush('off'))}>
            Turn on notifications
          </button>
        </p>
      )}
    </section>
  );
}
