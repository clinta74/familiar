// The avatar layer is framework-free on purpose (no React imports anywhere under src/avatar): the
// Android and iOS apps will host the same renderer in a WebView (MOBILE.md §1.1), and it moves to
// its own package when they do.

export type Emotion = 'neutral' | 'joy' | 'sad' | 'angry' | 'surprised' | 'relaxed' | 'thinking';

export type Gesture = 'nod' | 'shake' | 'tilt' | 'wave' | 'shrug' | 'laugh';

/** What the familiar is doing, which shapes its idle behaviour. */
export type AvatarState = 'idle' | 'listening' | 'thinking' | 'speaking' | 'resting';

export const emotions: readonly Emotion[] = ['neutral', 'joy', 'sad', 'angry', 'surprised', 'relaxed', 'thinking'];

export const gestures: readonly Gesture[] = ['nod', 'shake', 'tilt', 'wave', 'shrug', 'laugh'];

/** PLAN.md §5.3. Every renderer - the built-in plush llama, VRM, later 2D - implements this. */
export interface AvatarDriver {
  setState(state: AvatarState): void;
  /** Intensity 0-1, already scaled by the user's expressiveness setting. */
  setExpression(emotion: Emotion, intensity: number): void;
  playGesture(gesture: Gesture): void;
  /** Mouth openness 0-1. Decays on its own, so callers just keep feeding it while talking. */
  setMouthOpen(level: number): void;
  dispose(): void;
}
