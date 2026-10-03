import '../avatar/avatar.css';
import { characterInfo, characters, type Appearance, type CharacterModel } from '../avatar/appearance';
import { createAvatar } from '../avatar/createAvatar';
import type { Gesture } from '../avatar/types';
import type { Emotion, AvatarDriver } from '../avatar/types';

// The avatar gallery (gallery.html): every character rendered across moods, gestures, colours and
// accessories, for reviewing the characters side by side. Development only.
//
// Each cell is a frozen moment: the character's animation loop is stopped and stepped through
// simulated time, so the faces have settled and a gesture is caught mid-way, whatever the browser
// is doing; a 3D cell is then captured as an image and its WebGL context freed, so the page can hold
// any number of them.

/** The engine's internals the gallery steps by hand (plush/engine.ts). */
interface Steppable extends AvatarDriver {
  frame: number;
  last: number;
  tick: (now: number) => void;
  blinkAt: number;
  earTwitchAt: number;
  gazeAt: number;
  yawnAt: number;
  idleGaze: { x: number; y: number };
  gesture: { name: Gesture; start: number } | null;
  root: Element;
}

interface Moment {
  label: string;
  emotion?: Emotion;
  state?: 'idle' | 'thinking' | 'resting';
  gesture?: Gesture;
  /** How far through the gesture, 0-1. */
  at?: number;
  gaze?: [number, number];
}

const GESTURE_MS: Record<Gesture, number> = { nod: 900, shake: 900, tilt: 1600, wave: 1600, shrug: 1000, laugh: 1400, wink: 750, sigh: 1700, yawn: 2200, excited: 1300 };

const MOMENTS: Moment[] = [
  { label: 'neutral' },
  { label: 'happy', emotion: 'joy' },
  { label: 'sad', emotion: 'sad' },
  { label: 'cross', emotion: 'angry' },
  { label: 'surprised', emotion: 'surprised' },
  { label: 'relaxed', emotion: 'relaxed' },
  { label: 'thinking', state: 'thinking' },
  { label: 'resting', state: 'resting' },
  { label: 'wave', emotion: 'joy', gesture: 'wave', at: 0.45 },
  { label: 'shrug', gesture: 'shrug', at: 0.5 },
  { label: 'laugh', emotion: 'joy', gesture: 'laugh', at: 0.4 },
  { label: 'looking aside', gaze: [1.1, 0.1] },
];

const params = new URLSearchParams(location.search);
const chosen = (params.get('models')?.split(',') ?? characters.map((c) => c.id)) as CharacterModel[];
const styles = (params.get('style')?.split(',') ?? ['3d', '2d']) as ('2d' | '3d')[];
/** Just these moments (by label), and nothing else; and the cells' size. */
const only = params.get('moments')?.split(',');
const size = Number(params.get('size') ?? 220);
const gallery = document.getElementById('gallery')!;
document.documentElement.style.setProperty('--cell', `${size}px`);
const status = document.getElementById('status')!;

/** Settles a character into a moment by stepping its life through simulated time. */
function settle(avatar: Steppable, moment: Moment) {
  cancelAnimationFrame(avatar.frame);
  avatar.setState(moment.state ?? 'idle');
  avatar.setExpression(moment.emotion ?? 'neutral', 0.85);
  avatar.gesture = null;
  avatar.blinkAt = avatar.earTwitchAt = avatar.gazeAt = avatar.yawnAt = Infinity;
  avatar.idleGaze = { x: moment.gaze?.[0] ?? 0, y: moment.gaze?.[1] ?? 0 };
  const t0 = performance.now();
  avatar.last = t0;
  const steps = 40;
  const start = moment.gesture ? steps - Math.round((GESTURE_MS[moment.gesture] * (moment.at ?? 0.5)) / 100) : -1;
  for (let i = 1; i <= steps; i++) {
    if (moment.gesture && i === start) avatar.gesture = { name: moment.gesture, start: t0 + i * 100 };
    avatar.tick(t0 + i * 100);
    cancelAnimationFrame(avatar.frame);
  }
}

/** One figure: the character at a moment, as an image (3D) or a copy of its drawing (2D). */
function capture(avatar: Steppable, caption: string): HTMLElement {
  const figure = document.createElement('figure');
  if (avatar.root instanceof HTMLCanvasElement) {
    const img = document.createElement('img');
    img.src = avatar.root.toDataURL('image/png');
    img.alt = caption;
    figure.append(img);
  } else {
    const box = document.createElement('div');
    box.className = 'avatar-stage';
    box.append(avatar.root.cloneNode(true));
    figure.append(box);
  }
  const label = document.createElement('figcaption');
  label.textContent = caption;
  figure.append(label);
  return figure;
}

/** Renders one look across the moments (or just neutral), into a row. */
async function renderLook(row: HTMLElement, look: Appearance, moments: Moment[], prefix = '') {
  const stage = document.createElement('div');
  stage.className = 'avatar-stage';
  stage.style.cssText = `position:fixed;left:-2000px;top:0;width:${size}px;height:${Math.round(size * 1.2)}px`;
  document.body.append(stage);
  const avatar = (await createAvatar(stage, look)) as Steppable;
  for (const moment of moments) {
    settle(avatar, moment);
    row.append(capture(avatar, `${prefix}${moment.label}`));
    await new Promise((r) => setTimeout(r));
  }
  avatar.dispose();
  stage.remove();
}

async function main() {
  for (const model of chosen) {
    const info = characterInfo(model);
    for (const style of styles) {
      if (style === '3d' && !info.has3d) continue;
      const heading = document.createElement('h2');
      heading.textContent = `${info.name} — ${style === '3d' ? '3D' : 'flat'}`;
      const row = document.createElement('div');
      row.className = 'row';
      gallery.append(heading, row);
      status.textContent = `Rendering ${info.name} (${style})…`;

      const base: Appearance = { model, vrmId: null, colours: info.presets[0]!.colours, accessory: 'none', style };
      await renderLook(row, base, only ? MOMENTS.filter((m) => only.includes(m.label)) : MOMENTS);
      if (only) continue;
      for (const preset of info.presets.slice(1)) {
        await renderLook(row, { ...base, colours: preset.colours }, [MOMENTS[0]!], `${preset.name} · `);
      }
      for (const accessory of info.accessories.filter((a) => a !== 'none')) {
        await renderLook(row, { ...base, accessory }, [MOMENTS[1]!], `${accessory} · `);
      }
    }
  }
  status.textContent = 'Done.';
  document.body.dataset.done = 'true';
}

void main();
