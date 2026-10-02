import { useEffect, useState, type FormEvent } from 'react';
import { familiarApi, type FamiliarProfile } from '../api/familiar';

type Trait = 'warmth' | 'playfulness' | 'verbosity' | 'formality' | 'expressiveness';

const traits: { key: Trait; label: string; low: string; high: string }[] = [
  { key: 'warmth', label: 'Warmth', low: 'Reserved', high: 'Affectionate' },
  { key: 'playfulness', label: 'Playfulness', low: 'Earnest', high: 'Jokey' },
  { key: 'verbosity', label: 'Chattiness', low: 'Brief', high: 'Chatty' },
  { key: 'formality', label: 'Formality', low: 'Casual', high: 'Formal' },
  { key: 'expressiveness', label: 'Expressiveness', low: 'Subtle', high: 'Animated' },
];

/** Who your familiar is (PLAN.md §5.5). Changes apply from the next thing you say. */
export function FamiliarSettings() {
  const [profile, setProfile] = useState<FamiliarProfile | null>(null);
  const [status, setStatus] = useState<{ ok: boolean; message: string } | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    familiarApi.get().then(setProfile).catch((e: unknown) =>
      setStatus({ ok: false, message: e instanceof Error ? e.message : 'Could not load your familiar.' }));
  }, []);

  if (!profile) return status ? <p className="error">{status.message}</p> : <p className="muted">Loading…</p>;

  const update = (patch: Partial<FamiliarProfile>) => {
    setProfile({ ...profile, ...patch });
    setStatus(null);
  };

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!profile) return;
    setBusy(true);
    try {
      setProfile(await familiarApi.save(profile));
      setStatus({ ok: true, message: 'Saved. It applies from the next thing you say.' });
    } catch (e) {
      setStatus({ ok: false, message: e instanceof Error ? e.message : 'Could not save.' });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="stack">
      <div className="field-row">
        <label>
          Its name
          <input value={profile.name} onChange={(e) => update({ name: e.target.value })} maxLength={32} required />
        </label>
        <label>
          What it calls you
          <input
            value={profile.userNickname ?? ''}
            onChange={(e) => update({ userNickname: e.target.value || null })}
            placeholder="Your username"
            maxLength={32}
          />
        </label>
      </div>

      {traits.map(({ key, label, low, high }) => (
        <label key={key} className="slider">
          <span className="slider-label">{label}</span>
          <input
            type="range"
            min={0}
            max={1}
            step={0.05}
            value={profile[key]}
            onChange={(e) => update({ [key]: Number(e.target.value) })}
          />
          <span className="slider-ends muted"><span>{low}</span><span>{high}</span></span>
        </label>
      ))}

      {status && <p className={status.ok ? 'success' : 'error'} role="status">{status.message}</p>}
      <button type="submit" disabled={busy}>Save</button>
    </form>
  );
}
