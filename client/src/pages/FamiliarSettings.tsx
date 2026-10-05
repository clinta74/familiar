import { useEffect, useState, type FormEvent } from 'react';
import { familiarApi, type FamiliarProfile, type Voices } from '../api/familiar';
import { groupVoices } from './voiceNames';

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
  const [voices, setVoices] = useState<Voices | null>(null);

  useEffect(() => {
    familiarApi.voices().then(setVoices).catch(() => setVoices({ voices: [], default: '' }));
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

      <VoiceSettings profile={profile} voices={voices} onChange={update} />

      <label className="check">
        <input type="checkbox" checked={profile.proactive ?? true} onChange={(e) => update({ proactive: e.target.checked })} />
        <span>
          Say hello first
          <span className="muted"> — when you come back after a few hours, {profile.name || 'it'} greets you and mentions anything coming up soon.</span>
        </span>
      </label>

      <label className="check">
        <input type="checkbox" checked={profile.lookUps ?? false} onChange={(e) => update({ lookUps: e.target.checked })} />
        <span>
          Look things up
          <span className="muted">
            {' '}— the weather (Open-Meteo), what words mean (Wiktionary), facts and figures (Wikipedia, Wikidata) and public
            holidays (Nager.Date). Only those; what you ask goes to them, nothing else about you.
          </span>
        </span>
      </label>
      <label>
        Where you live <span className="muted small">(for the weather; a town is enough)</span>
        <input
          value={profile.homePlace ?? ''}
          onChange={(e) => update({ homePlace: e.target.value })}
          placeholder="e.g. Leeds"
          maxLength={80}
          disabled={!(profile.lookUps ?? false)}
        />
      </label>

      {status && <p className={status.ok ? 'success' : 'error'} role="status">{status.message}</p>}
      <button type="submit" disabled={busy} aria-busy={busy}>Save</button>
    </form>
  );
}

function VoiceSettings({ profile, voices, onChange }: {
  profile: FamiliarProfile;
  voices: Voices | null;
  onChange: (patch: Partial<FamiliarProfile>) => void;
}) {
  if (!voices) return null;
  if (voices.voices.length === 0) {
    return <p className="muted">Voice settings appear here when text-to-speech is running.</p>;
  }

  const current = profile.voice ?? voices.default;

  return (
    <div className="voice-row">
      <label>
        Voice
        <select value={current} onChange={(e) => onChange({ voice: e.target.value === voices.default ? null : e.target.value })}>
          {groupVoices(voices.voices).map(([language, options]) => (
            <optgroup key={language} label={language}>
              {options.map((v) => <option key={v.id} value={v.id}>{v.label}</option>)}
            </optgroup>
          ))}
        </select>
      </label>
      <label className="slider">
        <span className="slider-label">Speed</span>
        <input
          type="range"
          min={0.7}
          max={1.4}
          step={0.05}
          value={profile.voiceSpeed ?? 1}
          onChange={(e) => onChange({ voiceSpeed: Number(e.target.value) })}
        />
        <span className="slider-ends muted"><span>Slower</span><span>Faster</span></span>
      </label>
      <button type="button" className="secondary" onClick={() => void new Audio(familiarApi.previewUrl(current, profile.voiceSpeed ?? 1)).play()}>
        Preview
      </button>
    </div>
  );
}
