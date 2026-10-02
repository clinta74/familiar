import type { Emotion } from './types';

/** Everything the built-in avatar can do with its features. Neutral is the zero point for blending. */
export interface FaceParams {
  /** 0 closed, 1 normal, >1 wide. */
  eyeOpen: number;
  /** 0 normal eyes, 1 happy upward arcs. */
  eyeSmile: number;
  /** -1 lowered, 1 raised. */
  browRaise: number;
  /** -1 inner ends up (worried), 1 inner ends down (cross). */
  browTilt: number;
  /** -1 frown, 1 smile. */
  mouthCurve: number;
  mouthWidth: number;
  /** 0 closed, 1 wide open. */
  mouthOpen: number;
  blush: number;
  tear: number;
  /** Where the eyes look, -1..1 each way. */
  gazeX: number;
  gazeY: number;
  /** Ears: -1 drooping, 0 relaxed, 1 perked up. The llama's most readable feature. */
  earLift: number;
}

export const neutralFace: FaceParams = {
  eyeOpen: 1,
  eyeSmile: 0,
  browRaise: 0,
  browTilt: 0,
  mouthCurve: 0.3,
  mouthWidth: 1,
  mouthOpen: 0,
  blush: 0,
  tear: 0,
  gazeX: 0,
  gazeY: 0,
  earLift: 0,
};

/** Each emotion at full intensity. Weaker intensities blend from neutral towards these. */
export const emotionFaces: Record<Emotion, FaceParams> = {
  neutral: neutralFace,
  joy: { ...neutralFace, eyeOpen: 0.9, eyeSmile: 1, browRaise: 0.35, mouthCurve: 1, mouthWidth: 1.25, mouthOpen: 0.35, blush: 1, earLift: 0.7 },
  sad: { ...neutralFace, eyeOpen: 0.75, browRaise: 0.25, browTilt: -1, mouthCurve: -0.8, mouthWidth: 0.85, tear: 1, gazeY: 0.5, earLift: -1 },
  angry: { ...neutralFace, eyeOpen: 0.7, browRaise: -0.5, browTilt: 1, mouthCurve: -0.55, mouthWidth: 0.8, mouthOpen: 0.08, earLift: -0.6 },
  surprised: { ...neutralFace, eyeOpen: 1.35, browRaise: 1, mouthCurve: 0, mouthWidth: 0.55, mouthOpen: 1, earLift: 1 },
  relaxed: { ...neutralFace, eyeOpen: 0.55, eyeSmile: 0.45, browRaise: 0.05, mouthCurve: 0.65, mouthWidth: 1.05, earLift: -0.25 },
  thinking: { ...neutralFace, eyeOpen: 0.95, browRaise: 0.45, browTilt: -0.3, mouthCurve: -0.1, mouthWidth: 0.65, gazeX: 0.7, gazeY: -0.8, earLift: 0.3 },
};

const keys = Object.keys(neutralFace) as (keyof FaceParams)[];

/** The face for an emotion at an intensity: neutral blended towards the emotion's full face. */
export function faceFor(emotion: Emotion, intensity: number): FaceParams {
  const t = Math.min(1, Math.max(0, intensity));
  const target = emotionFaces[emotion];
  const out = { ...neutralFace };
  for (const key of keys) out[key] = neutralFace[key] + (target[key] - neutralFace[key]) * t;
  return out;
}

/** Moves `current` towards `target` with exponential smoothing; `rate` is roughly 1/seconds. */
export function approach(current: FaceParams, target: FaceParams, dt: number, rate: number): void {
  const k = 1 - Math.exp(-dt * rate);
  for (const key of keys) current[key] += (target[key] - current[key]) * k;
}
