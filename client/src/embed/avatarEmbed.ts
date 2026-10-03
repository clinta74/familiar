import '../avatar/avatar.css';
import { defaultAppearance, type Appearance } from '../avatar/appearance';
import { createAvatar } from '../avatar/createAvatar';
import type { AvatarDriver, AvatarState, Emotion, Gesture } from '../avatar/types';
import { visemeAt, type TimedWord } from '../avatar/visemes';

/**
 * The avatar on its own, for the phone apps to host in a WebView (MOBILE.md §1.1). The app bundles
 * this page and calls `window.familiarAvatar` with what the conversation stream says; the page
 * only draws. Audio is played natively: the app announces each clip's word timings once and then
 * feeds the loudness, and the mouth shapes are worked out here against the page's own clock.
 */
export interface AvatarEmbed {
  /** How it looks: the familiar profile's `appearance`. A different model swaps the renderer. */
  look(appearance: Appearance): void;
  state(state: AvatarState): void;
  expression(emotion: Emotion, intensity: number): void;
  gesture(gesture: Gesture): void;
  /** A piece of reply text with no audio: the mouth moves with the text. */
  text(fragment: string): void;
  /** A clip started playing `elapsedMs` ago; with word timings the mouth makes the words' shapes. */
  clip(words: TimedWord[] | null, elapsedMs: number): void;
  /** The voice's loudness now, 0-1, fed while a clip plays. */
  level(level: number): void;
  /** Nothing is being said. */
  silence(): void;
}

const stage = document.getElementById('stage')!;

let driver: AvatarDriver | null = null;
let model = '';
let appearance: Appearance = defaultAppearance;
let state: AvatarState = 'idle';
let building: Promise<void> = Promise.resolve();

let words: TimedWord[] | null = null;
let clipStart = 0;
let loudness = 0;
let frame = 0;

const modelOf = (a: Appearance) => (a.model === 'vrm' ? (a.vrmId ? `vrm:${a.vrmId}` : 'llama') : a.model);

/** Builds the renderer for the current look, replacing the last one; calls are serialised. */
function rebuild() {
  building = building.then(async () => {
    const next = await createAvatar(stage, appearance);
    driver?.dispose();
    driver = next;
    driver.setState(state);
  });
}

/** While a clip with timings plays: the shape of the word being said, at the voice's loudness. */
function animate() {
  cancelAnimationFrame(frame);
  if (!words) return;
  const tick = () => {
    if (!words) return;
    driver?.setViseme(visemeAt(words, (performance.now() - clipStart) / 1000), loudness);
    frame = requestAnimationFrame(tick);
  };
  frame = requestAnimationFrame(tick);
}

const embed: AvatarEmbed = {
  look(next) {
    appearance = { ...defaultAppearance, ...next };
    const nextModel = modelOf(appearance);
    if (nextModel !== model) {
      model = nextModel;
      rebuild();
    } else {
      driver?.setLook?.(appearance);
    }
  },
  state(next) {
    state = next;
    driver?.setState(next);
  },
  expression: (emotion, intensity) => driver?.setExpression(emotion, intensity),
  gesture: (gesture) => driver?.playGesture(gesture),
  text(fragment) {
    // As the web client does without audio: a pulse per fragment, quieter at punctuation.
    const length = fragment.trim().length;
    if (length === 0) return;
    driver?.setMouthOpen(/[.,!?;:]$/.test(fragment.trim()) ? 0.35 : Math.min(1, 0.45 + length / 12));
  },
  clip(timings, elapsedMs) {
    words = timings?.length ? timings : null;
    clipStart = performance.now() - elapsedMs;
    animate();
  },
  level(next) {
    loudness = Math.max(0, Math.min(1, next));
    if (!words) driver?.setMouthOpen(loudness);
  },
  silence() {
    words = null;
    loudness = 0;
    cancelAnimationFrame(frame);
    driver?.setViseme(null, 0);
    driver?.setMouthOpen(0);
  },
};

declare global {
  interface Window {
    familiarAvatar: AvatarEmbed;
  }
}

window.familiarAvatar = embed;
embed.look(defaultAppearance);
