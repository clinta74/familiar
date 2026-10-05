import type { PlushLook } from './plush/engine';
import type { Viseme } from './visemes';

// The avatar layer is framework-free on purpose (no React imports anywhere under src/avatar): the
// Android and iOS apps will host the same renderer in a WebView (MOBILE.md §1.1), and it moves to
// its own package when they do.

// What the familiar can feel and do: the server's (the generated protocol, MOBILE.md §6).
import type { Emotion, Gesture } from '../protocol/generated';

export type { Emotion, Gesture };

/** What the familiar is doing, which shapes its idle behaviour. */
export type AvatarState = 'idle' | 'listening' | 'thinking' | 'speaking' | 'resting';

export const emotions: readonly Emotion[] = ['neutral', 'joy', 'sad', 'angry', 'surprised', 'relaxed', 'thinking'];

export const gestures: readonly Gesture[] = ['nod', 'shake', 'tilt', 'wave', 'shrug', 'laugh', 'wink', 'sigh', 'yawn', 'excited'];

/** PLAN.md §5.3. Every renderer - the built-in plush llama, VRM, later 2D - implements this. */
export interface AvatarDriver {
  setState(state: AvatarState): void;
  /** Intensity 0-1, already scaled by the user's expressiveness setting. */
  setExpression(emotion: Emotion, intensity: number): void;
  playGesture(gesture: Gesture): void;
  /** Mouth openness 0-1. Decays on its own, so callers just keep feeding it while talking. */
  setMouthOpen(level: number): void;
  /**
   * The mouth shape being spoken now, from word timings, with `weight` 0-1 (the audio's loudness,
   * so silence still closes it). Null between words. Called every frame while speaking.
   */
  setViseme(viseme: Viseme | null, weight: number): void;
  /** Colours and accessory, for renderers that have them (the plush characters). Applied in place. */
  setLook?(look: PlushLook): void;
  /** Turns the whole character about its vertical axis (radians, 0 facing you), for 3D renderers. */
  setTurn?(radians: number): void;
  /** Someone's about (a tap, a key, the page coming back): rouses it if it was dozing off. */
  wake?(): void;
  dispose(): void;
}
