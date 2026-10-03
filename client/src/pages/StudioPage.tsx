import { useCallback, useEffect, useRef, useState, type ChangeEvent } from 'react';
import { api, errorFrom } from '../api/client';
import { familiarApi, type FamiliarProfile } from '../api/familiar';
import {
  characterInfo,
  characters,
  defaultAppearance,
  type Accessory,
  type Appearance,
  type CharacterModel,
  type LlamaColours,
} from '../avatar/appearance';
import { emotions, gestures, type Emotion, type Gesture } from '../avatar/types';
import { AvatarStage, type AvatarHandle } from '../components/AvatarStage';
import type { AvatarView } from '../protocol/generated';
import { Shell } from './Shell';
import { cachedLook, rememberLook } from '../avatar/lookCache';
import { useAuth } from '../auth/AuthContext';

/** A VRM model the user uploaded. */
type UploadedModel = AvatarView;

const avatarsApi = {
  list: () => api<UploadedModel[]>('/api/avatars'),
  remove: (id: string) => api<void>(`/api/avatars/${id}`, { method: 'DELETE' }),
  upload: async (file: File): Promise<UploadedModel> => {
    const response = await fetch(`/api/avatars?fileName=${encodeURIComponent(file.name)}`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'model/gltf-binary' },
      body: file,
    });
    if (!response.ok) throw await errorFrom(response);
    return (await response.json()) as UploadedModel;
  },
};

const accessoryLabels: Record<Accessory, string> = {
  none: 'Nothing',
  bow: 'Bow',
  scarf: 'Scarf',
  glasses: 'Glasses',
  flower: 'Flower',
};

const emotionLabels: Partial<Record<Emotion, string>> = {
  joy: 'Happy',
  sad: 'Sad',
  surprised: 'Surprised',
  angry: 'Cross',
  thinking: 'Thinking',
  relaxed: 'Relaxed',
};

const sameColours = (a: LlamaColours, b: LlamaColours) =>
  (Object.keys(a) as (keyof LlamaColours)[]).every((k) => a[k].toLowerCase() === b[k].toLowerCase());

/**
 * The studio (PLAN.md §5.5): change how the familiar looks and see it straight away. The plush
 * llama can be recoloured and dressed; or upload a VRM model (VRoid Studio makes them for free).
 */
export function StudioPage() {
  const avatar = useRef<AvatarHandle>(null);
  const [profile, setProfile] = useState<FamiliarProfile | null>(null);
  const [look, setLook] = useState<Appearance>(defaultAppearance);
  const [saved, setSaved] = useState<Appearance>(defaultAppearance);
  const [models, setModels] = useState<UploadedModel[]>([]);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<{ ok: boolean; message: string } | null>(null);
  /** How far the preview is turned round, in degrees: 3D looks can be seen from the side. */
  const [turn, setTurn] = useState(0);
  const drag = useRef<{ x: number; turn: number } | null>(null);
  const { account } = useAuth();
  const username = account?.username;
  /** What to show before the profile has loaded: the look as last seen here, or nothing yet. */
  const [early, setEarly] = useState(() => cachedLook(username));

  const load = useCallback(() => {
    familiarApi.get().then((p) => {
      // Always there when read; null only means "unchanged" when saving.
      const appearance = p.appearance ?? defaultAppearance;
      setProfile(p);
      setLook(appearance);
      setSaved(appearance);
      rememberLook(username, appearance);
    }).catch(() => {
      setStatus({ ok: false, message: 'Couldn’t load your familiar.' });
      setEarly((e) => e ?? defaultAppearance);
    });
    avatarsApi.list().then(setModels).catch(() => undefined);
  }, [username]);
  useEffect(load, [load]);

  const dirty = JSON.stringify(look) !== JSON.stringify(saved);

  async function save() {
    if (!profile) return;
    setBusy(true);
    setStatus(null);
    try {
      const updated = await familiarApi.save({ ...profile, appearance: look });
      const appearance = updated.appearance ?? look;
      setProfile(updated);
      setSaved(appearance);
      rememberLook(username, appearance);
      setStatus({ ok: true, message: 'Saved. This is how it looks everywhere now.' });
    } catch (e) {
      setStatus({ ok: false, message: e instanceof Error ? e.message : 'Couldn’t save.' });
    } finally {
      setBusy(false);
    }
  }

  async function upload(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    setBusy(true);
    setStatus(null);
    try {
      const model = await avatarsApi.upload(file);
      setModels((list) => [...list, model]);
      setLook((l) => ({ ...l, model: 'vrm', vrmId: model.id }));
      setStatus({ ok: true, message: `Uploaded “${model.name}”. Save to use it.` });
    } catch (e) {
      setStatus({ ok: false, message: e instanceof Error ? e.message : 'Couldn’t upload that.' });
    } finally {
      setBusy(false);
    }
  }

  async function removeModel(id: string) {
    if (!window.confirm('Delete this model?')) return;
    await avatarsApi.remove(id);
    setModels((list) => list.filter((m) => m.id !== id));
    // The server puts the llama back if it was in use.
    load();
  }

  const setColour = (key: keyof LlamaColours, value: string) =>
    setLook((l) => ({ ...l, colours: { ...l.colours, [key]: value } }));

  const builtIn = look.model !== 'vrm';
  const turnable = !builtIn || (look.style === '3d' && characterInfo(look.model as CharacterModel).has3d === true);
  const info = characterInfo(builtIn ? (look.model as CharacterModel) : 'llama');

  /** Switching character: its own default colours, unless the current ones are already its. */
  const choose = (model: CharacterModel) => setLook((l) => {
    const next = characterInfo(model);
    const keep = l.model === model || next.presets.some((p) => sameColours(p.colours, l.colours));
    return {
      ...l,
      model,
      vrmId: null,
      colours: keep ? l.colours : next.presets[0]!.colours,
      accessory: next.accessories.includes(l.accessory) ? l.accessory : 'none',
    };
  });

  return (
    <Shell>
      <div className="studio">
        <section className="stage-panel studio-stage" aria-label="Preview">
          {/* A 3D character can be turned round: drag across it, or use the slider. */}
          <div
            className={`studio-turn${turnable ? ' turnable' : ''}`}
            onPointerDown={(e) => {
              if (!turnable) return;
              drag.current = { x: e.clientX, turn };
              e.currentTarget.setPointerCapture(e.pointerId);
            }}
            onPointerMove={(e) => {
              if (!drag.current) return;
              const next = drag.current.turn + (e.clientX - drag.current.x) * 0.6;
              setTurn(Math.round(((next + 540) % 360) - 180));
            }}
            onPointerUp={() => (drag.current = null)}
            onPointerCancel={() => (drag.current = null)}
          >
            <AvatarStage ref={avatar} state="idle" appearance={profile ? look : early} turn={(turn * Math.PI) / 180} />
          </div>
          {turnable && (
            <div className="try-row turn-row">
              <label>
                Turn
                <input type="range" min={-180} max={180} step={5} value={turn} onChange={(e) => setTurn(Number(e.target.value))} />
              </label>
              <button type="button" className="link" onClick={() => setTurn(0)} disabled={turn === 0}>Face front</button>
            </div>
          )}
          <div className="try-row" aria-label="Try an expression">
            {emotions.filter((e) => emotionLabels[e]).map((e) => (
              <button key={e} type="button" className="chip" onClick={() => avatar.current?.express(e, 0.9)}>{emotionLabels[e]}</button>
            ))}
          </div>
          <div className="try-row" aria-label="Try a gesture">
            {gestures.map((g: Gesture) => (
              <button key={g} type="button" className="chip" onClick={() => avatar.current?.gesture(g)}>{g}</button>
            ))}
          </div>
        </section>

        <section className="card studio-controls">
          <h2>Look</h2>

          <fieldset className="stack">
            <legend>Model</legend>
            {characters.map((c) => (
              <label key={c.id} className="check">
                <input type="radio" name="model" checked={look.model === c.id} onChange={() => choose(c.id)} />
                <span>{c.name} <span className="muted">— {c.blurb}</span></span>
              </label>
            ))}
            {models.map((m) => (
              <div key={m.id} className="model-row">
                <label className="check">
                  <input
                    type="radio"
                    name="model"
                    checked={look.model === 'vrm' && look.vrmId === m.id}
                    onChange={() => setLook((l) => ({ ...l, model: 'vrm', vrmId: m.id }))}
                  />
                  <span>
                    {m.name}
                    <span className="muted">
                      {' '}— VRM {m.meta.version}
                      {m.meta.authors.length > 0 && ` by ${m.meta.authors.join(', ')}`}
                      {m.meta.licence && ` · licence: ${m.meta.licence}`}
                      {` · ${(m.sizeBytes / 1024 / 1024).toFixed(1)} MB`}
                    </span>
                  </span>
                </label>
                <button type="button" className="link" onClick={() => void removeModel(m.id)}>Delete</button>
              </div>
            ))}
            <label className="upload">
              <span className="secondary button-like">{busy ? 'Working…' : 'Upload a VRM model…'}</span>
              <input type="file" accept=".vrm,model/gltf-binary" onChange={(e) => void upload(e)} disabled={busy} hidden />
            </label>
            <p className="muted small">
              Make one for free in VRoid Studio and export it as .vrm (up to 40 MB). Check the model’s licence allows how you use it.
            </p>
          </fieldset>

          {builtIn && (
            <>
              {info.has3d && (
                <fieldset>
                  <legend>Style</legend>
                  <div className="try-row">
                    {(['2d', '3d'] as const).map((style) => (
                      <button
                        key={style}
                        type="button"
                        className={`chip${(look.style ?? '2d') === style ? ' selected' : ''}`}
                        aria-pressed={(look.style ?? '2d') === style}
                        onClick={() => setLook((l) => ({ ...l, style }))}
                      >
                        {style === '2d' ? 'Flat' : '3D'}
                      </button>
                    ))}
                  </div>
                  <p className="muted small">3D is modelled and lit, in soft fur. It needs a little more from the device; older phones show it flat.</p>
                </fieldset>
              )}

              <fieldset>
                <legend>Colours</legend>
                <div className="swatches">
                  {info.presets.map((p) => (
                    <button
                      key={p.name}
                      type="button"
                      className={`swatch${sameColours(p.colours, look.colours) ? ' selected' : ''}`}
                      onClick={() => setLook((l) => ({ ...l, colours: p.colours }))}
                      aria-pressed={sameColours(p.colours, look.colours)}
                    >
                      <span className="swatch-wool" style={{ background: p.colours.wool }}>
                        <span className="swatch-face" style={{ background: p.colours.face }} />
                      </span>
                      {p.name}
                    </button>
                  ))}
                </div>
                <div className="colour-pickers">
                  {(Object.keys(info.colourLabels) as (keyof LlamaColours)[]).map((key) => (
                    <label key={key}>
                      <input type="color" value={look.colours[key]} onChange={(e) => setColour(key, e.target.value)} />
                      {info.colourLabels[key]}
                    </label>
                  ))}
                </div>
              </fieldset>

              {info.accessories.length > 1 && (
                <fieldset>
                  <legend>Wearing</legend>
                  <div className="try-row">
                    {info.accessories.map((a) => (
                      <button
                        key={a}
                        type="button"
                        className={`chip${look.accessory === a ? ' selected' : ''}`}
                        aria-pressed={look.accessory === a}
                        onClick={() => setLook((l) => ({ ...l, accessory: a }))}
                      >
                        {accessoryLabels[a]}
                      </button>
                    ))}
                  </div>
                </fieldset>
              )}
            </>
          )}

          {status && <p className={status.ok ? 'success' : 'error'} role="status">{status.message}</p>}
          <div className="confirm-actions">
            <button type="button" onClick={() => void save()} disabled={busy || !dirty || !profile}>Save</button>
            <button type="button" className="secondary" onClick={() => setLook(saved)} disabled={!dirty}>Undo changes</button>
          </div>
          <p className="muted small">Name, voice and personality are in Settings.</p>
        </section>
      </div>
    </Shell>
  );
}
