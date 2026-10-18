import { useEffect, useState, type FormEvent } from 'react';
import { familiarApi, type FamiliarProfile, type Place, type Voices } from '../api/familiar';
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
            {' '}— the weather, air quality and pollen (Open-Meteo), what words mean (Wiktionary), facts and figures (Wikipedia,
            Wikidata), public holidays (Nager.Date), exchange rates (Frankfurter) and, in the US, your county and districts (the
            Census Bureau). Only those; what you ask goes to them,
            nothing else about you. Sums and unit conversions are worked out here either way.
          </span>
        </span>
      </label>
      <HomePicker profile={profile} onChange={update} />

      {status && <p className={status.ok ? 'success' : 'error'} role="status">{status.message}</p>}
      <button type="submit" disabled={busy} aria-busy={busy}>Save</button>
    </form>
  );
}

type Permission = 'granted' | 'prompt' | 'denied' | 'unknown';

/**
 * Where you live, as a real place (BACKLOG item 32): searched and picked, so "Warren" is the right
 * Warren, or found from this browser's location. The familiar is told it; the look-ups use it.
 */
function HomePicker({ profile, onChange }: { profile: FamiliarProfile; onChange: (patch: Partial<FamiliarProfile>) => void }) {
  const [query, setQuery] = useState('');
  const [matches, setMatches] = useState<Place[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [permission, setPermission] = useState<Permission>('unknown');

  useEffect(() => {
    let status: PermissionStatus | undefined;
    const read = () => setPermission((status?.state as Permission | undefined) ?? 'unknown');
    navigator.permissions?.query({ name: 'geolocation' }).then((s) => { status = s; read(); s.onchange = read; }).catch(() => undefined);
    return () => { if (status) status.onchange = null; };
  }, []);

  const pick = (place: Place) => {
    onChange({ home: place, homePlace: place.label });
    setMatches(null);
    setQuery('');
    setError(null);
  };

  async function search() {
    if (query.trim().length < 2) return;
    setBusy(true);
    setError(null);
    try {
      const found = await familiarApi.places(query.trim());
      setMatches(found);
      if (found.length === 0) setError('No places by that name. Try the town or city.');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not search for places.');
    } finally {
      setBusy(false);
    }
  }

  function useLocation() {
    if (!navigator.geolocation) {
      setError('This browser can’t tell where it is. Search for your town instead.');
      return;
    }
    setBusy(true);
    setError(null);
    navigator.geolocation.getCurrentPosition(
      (position) => {
        familiarApi.placeAt(position.coords.latitude, position.coords.longitude)
          .then(pick)
          .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Could not find a town there.'))
          .finally(() => setBusy(false));
      },
      (failure) => {
        setBusy(false);
        setError(failure.code === failure.PERMISSION_DENIED
          ? 'This browser isn’t sharing its location with Familiar. Allow it in the site settings (the icon left of the address), or search instead.'
          : 'This browser couldn’t tell where it is just now. Search for your town instead.');
      },
      { maximumAge: 10 * 60_000, timeout: 15_000 },
    );
  }

  const typedOnly = !profile.home && !!profile.homePlace;

  return (
    <fieldset className="home-picker stack">
      <legend>Where you live</legend>
      <p className="muted small">
        {profile.name || 'Your familiar'} knows it, for anything local, and the weather, air quality and holidays use it. Searching sends
        the name to Open-Meteo; “Use my location” sends this browser’s position, to about a kilometre, to OpenStreetMap.
      </p>
      {profile.homePlace ? (
        <div className="model-row">
          <span>
            <strong>{profile.homePlace}</strong>
            {typedOnly && <span className="error small"> · typed, not picked: search for it below so it’s the right place</span>}
          </span>
          <button type="button" className="link" onClick={() => onChange({ home: null, homePlace: '' })}>Clear</button>
        </div>
      ) : (
        <p className="muted small">Not set.</p>
      )}
      <div className="field-row">
        <input
          id="home-search"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); void search(); } }}
          placeholder={typedOnly ? profile.homePlace ?? '' : 'Town or city, e.g. Warren'}
          maxLength={80}
          aria-label="Search for your town"
        />
        <button type="button" className="secondary" disabled={busy || query.trim().length < 2} onClick={() => void search()}>Search</button>
        <button type="button" className="secondary" disabled={busy} onClick={useLocation}>Use my location</button>
      </div>
      {matches && matches.length > 0 && (
        <ul className="tool-list" aria-label="Places found">
          {matches.map((m) => (
            <li key={m.label}>
              <button type="button" className="link" onClick={() => pick(m)}>{m.label}</button>
            </li>
          ))}
        </ul>
      )}
      {error && <p className="error small" role="alert">{error}</p>}
      <p className="muted small">
        Location in this browser: {permission === 'granted' ? 'shared when the familiar asks where you are'
          : permission === 'denied' ? 'blocked. To allow it, use the site settings (the icon left of the address) and set Location to Allow'
          : permission === 'prompt' ? 'the browser asks the first time'
          : 'unknown'}. Your current location is used for that reply only, never kept.
      </p>
    </fieldset>
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
