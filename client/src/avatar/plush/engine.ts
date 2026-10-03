import type { Accessory, LlamaColours } from '../appearance';
import { approach, faceFor, neutralFace, type FaceParams } from '../faceExpressions';
import { emotions, type AvatarDriver, type AvatarState, type Emotion, type Gesture } from '../types';
import { visemeMouth, type Viseme } from '../visemes';

/** How long after a reply the emotion lingers at full strength before softening. */
const LINGER_MS = 8000;
/** What an emotion softens to once it has lingered. */
const SOFTENED = 0.4;

export const GESTURE_MS: Record<Gesture, number> = { nod: 900, shake: 900, tilt: 1600, wave: 1600, shrug: 1000, laugh: 1400, wink: 750, sigh: 1700, yawn: 2200, excited: 1300 };

/** How a character looks: its colours and what it's wearing (PLAN.md §5.5). */
export interface PlushLook {
  colours: LlamaColours;
  accessory: Accessory;
}

/** Everything a character needs to draw one frame. */
export interface Pose {
  /** Seconds, for slow cycles. */
  t: number;
  now: number;
  /** 1 normally, less when the user prefers reduced motion. */
  motion: number;
  state: AvatarState;
  emotion: Emotion;
  intensity: number;
  /** The blended face: eyes, brows, mouth, blush, tear, gaze, ear lift. */
  face: FaceParams;
  /**
   * How much of each emotion is showing now, 0-1, smoothed - for characters that show feelings
   * other than with a face (the spot's colour and surface).
   */
  mood: Record<Emotion, number>;
  /** How loud the voice is right now, 0-1, for characters that pulse rather than mouth words. */
  voice: number;
  /** Head movement: vertical offset, squash (+ taller), rotation in degrees. */
  dy: number;
  squash: number;
  rotate: number;
  browBoost: number;
  /** Extra swing for ears (or spines, or horns) beyond what earLift sets. */
  earWiggle: number;
  /** A paw waving from below: how far up (0-1), or null when it isn't. */
  pawLift: number | null;
  pawAngle: number;
  /** 1 open, dipping to 0 mid-blink. */
  blink: number;
  /** How closed the right eye is for a wink. */
  wink: number;
  /** The gesture under way and how far through it (0-1), if any. */
  gesture: { name: Gesture; u: number } | null;
}

const zeroMood = (): Record<Emotion, number> => Object.fromEntries(emotions.map((e) => [e, e === 'neutral' ? 1 : 0])) as Record<Emotion, number>;

/**
 * The life shared by the plush characters: it blends between emotions, lingers on a feeling and
 * lets it soften, blinks, glances about, twitches, breathes, plays gestures and moves its mouth with
 * speech. A character supplies the drawing - what an ear lift or a nod looks like on it.
 */
export abstract class PlushCharacter implements AvatarDriver {
  protected readonly face: FaceParams = { ...neutralFace };
  protected readonly mood = zeroMood();
  private readonly reducedMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;

  private emotion: Emotion = 'neutral';
  private intensity = 0.5;
  private state: AvatarState = 'idle';
  private idleSince = performance.now();
  private mouth = 0;
  private voice = 0;
  private viseme: Viseme | null = null;
  private visemeWeight = 0;
  private gesture: { name: Gesture; start: number } | null = null;
  private blinkAt = performance.now() + 2500;
  private gazeAt = performance.now() + 3000;
  private earTwitchAt = performance.now() + 5000;
  private yawnAt = performance.now() + 6000;
  private idleGaze = { x: 0, y: 0 };
  private last = performance.now();
  private frame = 0;

  /** The character's SVG (or the 3D characters' canvas), removed on dispose. */
  protected abstract readonly root: Element;

  abstract setLook(look: PlushLook): void;

  /** Draws one frame. */
  protected abstract draw(pose: Pose): void;

  /** Starts the animation loop. Call at the end of the subclass constructor, once parts exist. */
  protected start() {
    this.frame = requestAnimationFrame(this.tick);
  }

  setState(state: AvatarState): void {
    if (state === 'idle' && this.state !== 'idle') this.idleSince = performance.now();
    this.state = state;
  }

  setExpression(emotion: Emotion, intensity: number): void {
    this.emotion = emotion;
    this.intensity = intensity;
    this.idleSince = performance.now();
  }

  playGesture(gesture: Gesture): void {
    this.gesture = { name: gesture, start: performance.now() };
  }

  setMouthOpen(level: number): void {
    const l = Math.min(1, level);
    this.mouth = Math.max(this.mouth, l);
    this.voice = Math.max(this.voice, l);
  }

  setViseme(viseme: Viseme | null, weight: number): void {
    this.viseme = viseme;
    this.visemeWeight = Math.min(1, Math.max(0, weight));
    this.voice = Math.max(this.voice, this.visemeWeight);
  }

  dispose(): void {
    cancelAnimationFrame(this.frame);
    this.root.remove();
  }

  private readonly tick = (now: number) => {
    // Never backwards: a negative step would make the easing run away.
    const dt = Math.min(0.1, Math.max(0, (now - this.last) / 1000));
    this.last = now;
    this.approachTarget(now, dt);
    this.draw(this.pose(now));
    this.frame = requestAnimationFrame(this.tick);
  };

  private approachTarget(now: number, dt: number) {
    const lingering = this.state === 'idle' && now - this.idleSince > LINGER_MS;
    const intensity = lingering ? this.intensity * SOFTENED : this.intensity;
    const emotion: Emotion = this.state === 'thinking' ? 'thinking' : this.emotion;
    const strength = this.state === 'thinking' ? 0.8 : intensity;
    const target = faceFor(emotion, strength);

    // The mood: the current emotion at its strength, everything else fading out.
    const k = 1 - Math.exp(-dt * 4);
    for (const e of emotions) {
      const goal = e === emotion ? strength : e === 'neutral' ? 1 - strength : 0;
      this.mood[e] += (goal - this.mood[e]) * k;
    }

    if (this.state === 'listening') {
      target.browRaise += 0.25;
      target.eyeOpen += 0.1;
      target.earLift = Math.max(target.earLift, 0.6);
    }

    // A resting familiar yawns now and then.
    if (this.state === 'resting' && !this.gesture && now > this.yawnAt) {
      this.playGesture('yawn');
      this.yawnAt = now + 9000 + Math.random() * 9000;
    }

    if (this.state === 'resting') {
      target.eyeOpen = 0.05;
      target.mouthCurve = 0.2;
      target.earLift = -0.7;
    }

    if (now > this.gazeAt) {
      this.idleGaze = Math.random() < 0.4 ? { x: 0, y: 0 } : { x: Math.random() * 1.4 - 0.7, y: Math.random() * 0.8 - 0.4 };
      this.gazeAt = now + 1800 + Math.random() * 3500;
    }
    if (emotion !== 'thinking' && emotion !== 'sad') {
      target.gazeX += this.idleGaze.x;
      target.gazeY += this.idleGaze.y;
    }

    // Laughing takes over the face: eyes squeezed into ^ ^, a big open grin, ears up.
    if (this.gesture?.name === 'laugh') {
      const u = (now - this.gesture.start) / GESTURE_MS.laugh;
      const env = Math.sin(Math.PI * Math.min(1, u));
      target.eyeSmile = Math.max(target.eyeSmile, env);
      target.mouthCurve = Math.max(target.mouthCurve, env);
      target.mouthWidth = Math.max(target.mouthWidth, 1 + 0.25 * env);
      target.mouthOpen = Math.max(target.mouthOpen, (0.45 + 0.25 * Math.abs(Math.sin(u * Math.PI * 7))) * env);
      target.blush = Math.max(target.blush, env);
      target.earLift = Math.max(target.earLift, 0.6 * env);
    }

    // The other gestures that change the face, not just the head's movement.
    if (this.gesture && this.gesture.name !== 'laugh') {
      const u = Math.min(1, (now - this.gesture.start) / GESTURE_MS[this.gesture.name]);
      const env = Math.sin(Math.PI * u);
      switch (this.gesture.name) {
        case 'wink':
          // The right eye's lid is closed when drawn; here, the grin that goes with it.
          target.mouthCurve = Math.max(target.mouthCurve, 0.9 * env);
          target.blush = Math.max(target.blush, 0.4 * env);
          break;
        case 'sigh':
          // Deflating: eyes half-closed, ears sag, a small "haah".
          target.eyeOpen = Math.min(target.eyeOpen, 1 - 0.5 * env);
          target.earLift = Math.min(target.earLift, -0.7 * env);
          target.mouthOpen = Math.max(target.mouthOpen, 0.25 * env);
          target.mouthWidth = Math.min(target.mouthWidth, 1 - 0.3 * env);
          target.browTilt = Math.min(target.browTilt, -0.5 * env);
          break;
        case 'yawn': {
          // A slow, enormous yawn: mouth wide and round, eyes squeezed, ears droop.
          const wide = Math.sin(Math.PI * Math.min(1, u * 1.15)) ** 0.6;
          target.mouthOpen = Math.max(target.mouthOpen, 1.8 * wide);
          target.mouthWidth = Math.min(target.mouthWidth, 1 - 0.25 * wide);
          target.mouthCurve = 0;
          target.eyeOpen = Math.min(target.eyeOpen, 1 - 0.85 * wide);
          target.earLift = Math.min(target.earLift, -0.5 * wide);
          break;
        }
        case 'excited':
          target.eyeOpen = Math.max(target.eyeOpen, 1 + 0.25 * env);
          target.mouthOpen = Math.max(target.mouthOpen, 0.5 * env);
          target.mouthCurve = Math.max(target.mouthCurve, env);
          target.blush = Math.max(target.blush, 0.8 * env);
          target.earLift = Math.max(target.earLift, env);
          break;
      }
    }

    this.mouth *= Math.exp(-dt * 9);
    this.voice *= Math.exp(-dt * 7);
    target.mouthOpen = Math.max(target.mouthOpen, this.mouth * 0.9);

    approach(this.face, target, dt, 8);

    // Speech: the shape of the sound being made, scaled by how loud it is. Letters last ~60 ms, so
    // the mouth follows much faster than expressions do; a pause between words closes it.
    this.visemeWeight *= Math.exp(-dt * 6);
    if (this.visemeWeight > 0.02) {
      const shape = this.viseme ? visemeMouth[this.viseme] : visemeMouth.closed;
      const kk = 1 - Math.exp(-dt * 28);
      const w = Math.min(1, 0.4 + this.visemeWeight);
      this.face.mouthOpen += (shape.open * w - this.face.mouthOpen) * kk;
      this.face.mouthWidth += (shape.width - this.face.mouthWidth) * kk * w;
      this.face.mouthCurve += (Math.max(shape.curve, target.mouthCurve * 0.6) - this.face.mouthCurve) * kk * w;
    }
  }

  /** Breathing, emotion energy and gestures, as head movement shared by every character. */
  private pose(now: number): Pose {
    const motion = this.reducedMotion ? 0.3 : 1;
    const t = now / 1000;

    let blink = 1;
    if (now > this.blinkAt) {
      const into = now - this.blinkAt;
      blink = into < 80 ? 1 - into / 80 : into < 170 ? (into - 80) / 90 : 1;
      if (into >= 170) this.blinkAt = now + 2400 + Math.random() * 4200;
    }

    let dy = Math.sin(t * Math.PI * 2 / 3.8) * 2.5 * motion;
    let squash = Math.sin(t * Math.PI * 2 / 3.8) * 0.01 * motion;
    let rotate = 0;
    let browBoost = 0;
    let earWiggle = 0;
    let pawLift: number | null = null;
    let pawAngle = 0;
    let wink = 0;
    let gesture: Pose['gesture'] = null;

    if (this.emotion === 'joy' && this.state !== 'resting') {
      const hop = Math.abs(Math.sin(t * Math.PI * 2 * 1.3)) * this.intensity * motion;
      dy -= hop * 5;
      earWiggle += hop * 6;
    }
    if (this.emotion === 'sad') dy += 5 * this.intensity;
    if (this.state === 'thinking') rotate += Math.sin(t * 2) * 3 * motion - 7;

    // An occasional twitch keeps it alive while idle.
    if (now > this.earTwitchAt) {
      const into = now - this.earTwitchAt;
      earWiggle += Math.sin((into / 300) * Math.PI) * 8 * motion;
      if (into > 300) this.earTwitchAt = now + 4000 + Math.random() * 7000;
    }

    if (this.gesture) {
      const u = (now - this.gesture.start) / GESTURE_MS[this.gesture.name];
      if (u >= 1) {
        this.gesture = null;
      } else {
        gesture = { name: this.gesture.name, u };
        const env = Math.sin(Math.PI * u);
        switch (this.gesture.name) {
          case 'nod':
            dy += Math.sin(u * Math.PI * 4) * 7 * motion;
            break;
          case 'shake':
            rotate += Math.sin(u * Math.PI * 6) * 8 * env * motion;
            earWiggle += Math.sin(u * Math.PI * 6) * 10 * env * motion;
            break;
          case 'tilt':
            rotate += 13 * env;
            break;
          case 'wave':
            pawLift = env;
            pawAngle = Math.sin(u * Math.PI * 8) * 22 * env;
            rotate -= 5 * env;
            break;
          case 'shrug':
            squash -= 0.06 * env;
            browBoost = 0.7 * env;
            earWiggle -= 12 * env;
            break;
          case 'wink':
            rotate += 6 * env;
            wink = Math.sin(Math.PI * Math.min(1, u)) ** 0.5;
            break;
          case 'sigh':
            dy += 7 * env;
            squash += 0.04 * env;
            break;
          case 'yawn':
            rotate -= 7 * env;
            dy -= 3 * env;
            squash -= 0.04 * env;
            break;
          case 'excited': {
            // Two big hops.
            const hop = Math.abs(Math.sin(u * Math.PI * 2));
            dy -= hop * 16 * env * motion;
            squash -= (hop - 0.5) * 0.06 * env * motion;
            earWiggle += Math.sin(u * Math.PI * 4) * 6 * env * motion;
            break;
          }
          case 'laugh': {
            // Quick bouncing chuckles, the head rocking back a little.
            const chuckle = Math.abs(Math.sin(u * Math.PI * 7));
            dy -= chuckle * 6 * env * motion;
            squash += chuckle * 0.03 * env * motion;
            rotate += Math.sin(u * Math.PI * 3.5) * 4 * env * motion;
            earWiggle += Math.sin(u * Math.PI * 7) * 9 * env * motion;
            break;
          }
        }
      }
    }

    return {
      t, now, motion,
      state: this.state, emotion: this.emotion, intensity: this.intensity,
      face: this.face, mood: this.mood, voice: this.voice,
      dy, squash, rotate, browBoost, earWiggle, pawLift, pawAngle, blink, wink, gesture,
    };
  }
}
