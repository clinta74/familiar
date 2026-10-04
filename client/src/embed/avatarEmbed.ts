import '../avatar/avatar.css';
import { defaultAppearance, type Appearance } from '../avatar/appearance';
import { createAvatar } from '../avatar/createAvatar';
import type { AvatarDriver, AvatarState, Emotion, Gesture } from '../avatar/types';
import { visemeAt, type TimedWord } from '../avatar/visemes';
import type { SoundEffect } from '../protocol/generated';
import { playSoundEffect } from '../voice/soundEffects';

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
  /** Turned round this far (radians; 0 faces front): the studio shows a 3D character from the side. */
  turn(radians: number): void;
  /** A sound effect, made here (the app plays the voice natively; effects are synthesised). */
  sound(sound: SoundEffect): void;
}

const stage = document.getElementById('stage')!;

let driver: AvatarDriver | null = null;
let model = '';
let appearance: Appearance = defaultAppearance;
let state: AvatarState = 'idle';
let turned = 0;
let effects: AudioContext | null = null;
let building: Promise<void> = Promise.resolve();

let words: TimedWord[] | null = null;
let clipStart = 0;
let loudness = 0;
let frame = 0;

const modelOf = (a: Appearance) => (a.model === 'vrm' ? (a.vrmId ? `vrm:${a.vrmId}` : 'llama') : `${a.model}:${a.style ?? '2d'}`);

/**
 * Builds the renderer for the current look, replacing the last one; calls are serialised. A glow
 * holds the place while it's built (avatar.html), and the character fades in.
 */
function rebuild() {
  building = building.then(async () => {
    stage.dataset.loading = '';
    driver?.dispose();
    driver = null;
    const next = await createAvatar(stage, appearance);
    driver = next;
    driver.setState(state);
    driver.setTurn?.(turned);
    delete stage.dataset.loading;
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
  sound(sound) {
    effects ??= new AudioContext();
    void effects.resume();
    playSoundEffect(effects, sound);
  },
  turn(radians) {
    turned = radians;
    driver?.setTurn?.(radians);
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
// Nothing is drawn until the app says how it looks (it remembers the last look, so that's quick):
// not a stand-in llama that then swaps.

