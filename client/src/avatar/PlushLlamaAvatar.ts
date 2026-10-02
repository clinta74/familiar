import { approach, faceFor, neutralFace, type FaceParams } from './faceExpressions';
import type { AvatarDriver, AvatarState, Emotion, Gesture } from './types';

const SVG = 'http://www.w3.org/2000/svg';

/** How long after a reply the emotion lingers at full strength before softening. */
const LINGER_MS = 8000;
/** What an emotion softens to once it has lingered. */
const SOFTENED = 0.4;

const GESTURE_MS: Record<Gesture, number> = { nod: 900, shake: 900, tilt: 1600, wave: 1600, shrug: 1000, laugh: 1400, wink: 750, sigh: 1700, yawn: 2200, excited: 1300 };

// Feature positions, in viewBox units. The head is centred on the origin; the muzzle sits below,
// and the neck runs from behind the head down past the bottom of the stage.
const EYE_X = 23;
const EYE_Y = -6;
const EYE_R = 12.5;
const EAR_X = 42;
const EAR_Y = -56;
const MOUTH_Y = 42;
/** Where the head pivots on the neck for tilts and shakes. */
const NECK_PIVOT_Y = 60;

let instances = 0;

function el<K extends keyof SVGElementTagNameMap>(name: K, attrs: Record<string, string | number> = {}, parent?: Element) {
  const node = document.createElementNS(SVG, name);
  for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, String(value));
  parent?.appendChild(node);
  return node;
}

interface Eye {
  button: SVGGElement;
  lid: SVGRectElement;
  happy: SVGPathElement;
}

interface Parts {
  root: SVGSVGElement;
  /** Everything that moves with the head. The neck stays put, as a neck should. */
  head: SVGGElement;
  ears: [SVGGElement, SVGGElement];
  tuft: SVGGElement;
  eyes: [Eye, Eye];
  brows: [SVGLineElement, SVGLineElement];
  mouth: SVGPathElement;
  cheeks: SVGGElement;
  tear: SVGPathElement;
  thinking: SVGGElement;
  hoof: SVGGElement;
  sleep: SVGTextElement;
}

type Puff = [x: number, y: number, r: number];

/**
 * A cloud of fleece: overlapping puffs, drawn twice - slightly larger in the shadow colour first,
 * then in wool - so only the outer edge of the cloud gets an outline, the way a cartoon fleece does.
 */
function wool(parent: SVGGElement, puffs: Puff[], className = ''): SVGGElement {
  const g = el('g', { class: `plush-fuzzy ${className}`.trim() }, parent);
  for (const [cx, cy, r] of puffs) el('circle', { cx, cy, r: r + 2.6, class: 'plush-wool-outline' }, g);
  for (const [cx, cy, r] of puffs) el('circle', { cx, cy, r, class: 'plush-wool' }, g);
  return g;
}

/** Little "c" curls stitched into the fleece. */
function curls(parent: SVGGElement, at: [x: number, y: number, flip?: boolean][]) {
  const d = at.map(([x, y, flip]) => (flip
    ? `M${x + 5},${y} A5,5 0 1 0 ${x - 3},${y + 4}`
    : `M${x - 5},${y} A5,5 0 1 1 ${x + 3},${y + 4}`)).join(' ');
  el('path', { class: 'plush-curl', d }, parent);
}

/** A tall llama ear, base at the origin, pointing up. */
function ear(parent: SVGGElement): SVGGElement {
  const g = el('g', {}, parent);
  const outline = 'M-15,8 C-24,-26 -17,-76 0,-98 C17,-76 24,-26 15,8 Z';
  el('path', { class: 'plush-wool plush-ear-outer plush-fuzzy', d: outline }, g);
  el('path', { class: 'plush-ear-inner', d: 'M-8,2 C-13,-24 -10,-60 0,-78 C10,-60 13,-24 8,2 Z' }, g);
  return g;
}

function buttonEye(parent: SVGGElement, cx: number, clipId: string): Eye {
  const clip = el('clipPath', { id: clipId }, parent);
  el('circle', { cx, cy: EYE_Y, r: EYE_R + 0.5 }, clip);

  const button = el('g', {}, parent);
  el('circle', { cx, cy: EYE_Y, r: EYE_R, class: 'plush-button' }, button);
  el('circle', { cx: cx + 3.6, cy: EYE_Y - 4, r: 3.4, class: 'plush-button-shine' }, button);
  el('circle', { cx: cx - 4, cy: EYE_Y + 4.2, r: 1.4, class: 'plush-button-shine' }, button);

  // A felt lid that slides down over the button: closing, sleepy, cross.
  const lidGroup = el('g', { 'clip-path': `url(#${clipId})` }, button);
  const lid = el('rect', { x: cx - EYE_R - 1, width: EYE_R * 2 + 2, height: EYE_R * 2 + 2, class: 'plush-lid' }, lidGroup);

  // Embroidered ^ for a happy, eyes-closed smile.
  const happy = el('path', { class: 'plush-thread', d: `M${cx - 10},${EYE_Y + 3} Q${cx},${EYE_Y - 10} ${cx + 10},${EYE_Y + 3}` }, parent);

  // Lashes at the outer corner. Llamas have spectacular lashes.
  const side = Math.sign(cx);
  const x = (dx: number) => cx + side * dx;
  el('path', {
    class: 'plush-lash',
    d: `M${x(7)},${EYE_Y - 8} L${x(11)},${EYE_Y - 14} M${x(10)},${EYE_Y - 4} L${x(16)},${EYE_Y - 8} M${x(11.5)},${EYE_Y + 1} L${x(17.5)},${EYE_Y - 1}`,
  }, parent);

  return { button, lid, happy };
}

function build(container: HTMLElement): Parts {
  const id = `plush-${++instances}`;
  const root = el('svg', { viewBox: '-122 -182 244 330', class: 'plush-llama', role: 'img', 'aria-label': 'Your familiar, a plush llama' });

  // Plush: a little noise displaces the silhouettes' edges so they read as fuzzy fabric, not vector.
  const defs = el('defs', {}, root);
  const fuzz = el('filter', { id: `${id}-fuzz`, x: '-10%', y: '-10%', width: '120%', height: '120%' }, defs);
  el('feTurbulence', { type: 'fractalNoise', baseFrequency: 0.8, numOctaves: 2, seed: 7, result: 'noise' }, fuzz);
  el('feDisplacementMap', { in: 'SourceGraphic', in2: 'noise', scale: 3, xChannelSelector: 'R', yChannelSelector: 'G' }, fuzz);
  root.style.setProperty('--plush-fuzz', `url(#${id}-fuzz)`);

  // The long, fleecy neck: starts behind the head and runs down past the bottom of the stage,
  // which fades it out.
  const neck = el('g', {}, root);
  const neckPuffs: Puff[] = [];
  for (let y = 70; y <= 640; y += 26) {
    neckPuffs.push([-50, y, 15], [50, y + 13, 15]);
  }
  wool(neck, neckPuffs);
  el('rect', { x: -52, y: 50, width: 104, height: 600, class: 'plush-wool plush-fuzzy' }, neck);
  curls(neck, [[-24, 120], [20, 150, true], [-6, 196], [26, 236, true], [-28, 262], [6, 300]]);

  const head = el('g', {}, root);
  const ears: [SVGGElement, SVGGElement] = [ear(head), ear(head)];

  // The mane: a ring of fleece framing the face, open at the bottom where it becomes the neck.
  const mane: Puff[] = [];
  for (let i = 0; i < 26; i++) {
    const a = (i / 26) * Math.PI * 2;
    mane.push([Math.cos(a) * 60, 8 + Math.sin(a) * 66, i % 2 === 0 ? 18 : 16]);
  }
  wool(head, mane);
  curls(head, [[-66, 0], [62, 18, true], [-58, 46], [56, 56, true]]);

  // The face: bare, soft taupe, set inside the fleece.
  el('ellipse', { cx: 0, cy: 12, rx: 47, ry: 52, class: 'plush-skin plush-fuzzy' }, head);

  // The fringe: a big puffy topknot falling to just above the eyes.
  const tuft = wool(head, [
    [-38, -44, 15], [-24, -60, 18], [-5, -68, 20], [15, -66, 19], [33, -54, 17], [42, -38, 13],
    [-28, -36, 14], [-9, -42, 16], [11, -40, 16], [28, -34, 13],
  ]);
  curls(tuft, [[-26, -58], [9, -66, true], [30, -50], [-7, -44], [20, -38, true]]);

  const cheeks = el('g', { class: 'plush-cheeks' }, head);
  el('ellipse', { cx: -35, cy: 24, rx: 9.5, ry: 6.5 }, cheeks);
  el('ellipse', { cx: 35, cy: 24, rx: 9.5, ry: 6.5 }, cheeks);

  const eyes: [Eye, Eye] = [buttonEye(head, -EYE_X, `${id}-eye-l`), buttonEye(head, EYE_X, `${id}-eye-r`)];
  const brows: [SVGLineElement, SVGLineElement] = [el('line', { class: 'plush-thread' }, head), el('line', { class: 'plush-thread' }, head)];

  // The snout: a rounded muzzle with a soft nose and a split upper lip running down to the mouth.
  el('ellipse', { cx: 0, cy: 36, rx: 27, ry: 22, class: 'plush-snout' }, head);
  el('path', { class: 'plush-nose', d: 'M-10,22 C-10,16 10,16 10,22 C10,28 4,32 0,32 C-4,32 -10,28 -10,22 Z' }, head);
  el('path', { class: 'plush-nose-shine', d: 'M-6,21 Q-4,19 -2,21 M2,21 Q4,19 6,21' }, head);
  el('path', { class: 'plush-thread', d: 'M0,32 L0,40' }, head);
  const mouth = el('path', { class: 'plush-mouth' }, head);

  const tear = el('path', { class: 'plush-tear', d: 'M0,-7 C4,-1 6,3 6,6 A6,6 0 0 1 -6,6 C-6,3 -4,-1 0,-7 Z' }, head);

  // A fleecy hoof that pops up from below the stage to wave.
  const hoof = el('g', {}, root);
  wool(hoof, [[0, 40, 15], [0, 18, 15], [0, 62, 15]]);
  el('path', { class: 'plush-hoof', d: 'M-12,2 C-12,-10 -6,-14 0,-14 C6,-14 12,-10 12,2 C6,-2 -6,-2 -12,2 Z' }, hoof);
  el('path', { class: 'plush-hoof-split', d: 'M0,-14 L0,-4' }, hoof);

  const thinking = el('g', { class: 'plush-thinking' }, root);
  [[72, -128, 5], [88, -146, 7], [106, -166, 9]].forEach(([cx, cy, r]) => el('circle', { cx: cx!, cy: cy!, r: r! }, thinking));

  const sleep = el('text', { x: 68, y: -112, class: 'plush-sleep', 'aria-hidden': 'true' }, root);
  sleep.textContent = 'z';

  container.appendChild(root);
  return { root, head, ears, tuft, eyes, brows, mouth, cheeks, tear, thinking, hoof, sleep };
}

const f = (n: number) => n.toFixed(2);

/**
 * The default familiar: a plush llama head, drawn in SVG. No assets, so it works offline, costs
 * nothing to load and carries no model licence; a VRM model replaces it when one is supplied
 * (src/avatar/createAvatar.ts). Its ears do a lot of the emotional work: perked for joy and
 * surprise, drooping when sad, pinned back when cross.
 */
export class PlushLlamaAvatar implements AvatarDriver {
  private readonly parts: Parts;
  private readonly face: FaceParams = { ...neutralFace };
  private readonly reducedMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;

  private emotion: Emotion = 'neutral';
  private intensity = 0.5;
  private state: AvatarState = 'idle';
  private idleSince = performance.now();
  private mouth = 0;
  private gesture: { name: Gesture; start: number } | null = null;
  private blinkAt = performance.now() + 2500;
  private gazeAt = performance.now() + 3000;
  private earTwitchAt = performance.now() + 5000;
  private yawnAt = performance.now() + 6000;
  private idleGaze = { x: 0, y: 0 };
  private last = performance.now();
  private frame = 0;

  constructor(container: HTMLElement) {
    this.parts = build(container);
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
    this.mouth = Math.max(this.mouth, Math.min(1, level));
  }

  dispose(): void {
    cancelAnimationFrame(this.frame);
    this.parts.root.remove();
  }

  private readonly tick = (now: number) => {
    const dt = Math.min(0.1, (now - this.last) / 1000);
    this.last = now;
    this.approachTarget(now, dt);
    this.render(now);
    this.frame = requestAnimationFrame(this.tick);
  };

  private approachTarget(now: number, dt: number) {
    const lingering = this.state === 'idle' && now - this.idleSince > LINGER_MS;
    const intensity = lingering ? this.intensity * SOFTENED : this.intensity;
    const emotion: Emotion = this.state === 'thinking' ? 'thinking' : this.emotion;
    const target = faceFor(emotion, this.state === 'thinking' ? 0.8 : intensity);

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
          // The right eye's lid is closed in render(); here, the grin that goes with it.
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
    target.mouthOpen = Math.max(target.mouthOpen, this.mouth * 0.9);

    approach(this.face, target, dt, 8);
  }

  private render(now: number) {
    const p = this.parts;
    const face = this.face;
    const motion = this.reducedMotion ? 0.3 : 1;
    const t = now / 1000;

    let blink = 1;
    if (now > this.blinkAt) {
      const into = now - this.blinkAt;
      blink = into < 80 ? 1 - into / 80 : into < 170 ? (into - 80) / 90 : 1;
      if (into >= 170) this.blinkAt = now + 2400 + Math.random() * 4200;
    }

    // Body: breathing, emotion energy, gestures. Rotation pivots at the neck, like a head on a toy.
    let dy = Math.sin(t * Math.PI * 2 / 3.8) * 2.5 * motion;
    let squash = Math.sin(t * Math.PI * 2 / 3.8) * 0.01 * motion;
    let rotate = 0;
    let browBoost = 0;
    let earWiggle = 0;
    let hoofLift: number | null = null;
    let hoofAngle = 0;

    if (this.emotion === 'joy' && this.state !== 'resting') {
      const hop = Math.abs(Math.sin(t * Math.PI * 2 * 1.3)) * this.intensity * motion;
      dy -= hop * 5;
      earWiggle += hop * 6;
    }
    if (this.emotion === 'sad') dy += 5 * this.intensity;
    if (this.state === 'thinking') rotate += Math.sin(t * 2) * 3 * motion - 7;

    // An occasional ear twitch keeps it alive while idle.
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
            hoofLift = env;
            hoofAngle = Math.sin(u * Math.PI * 8) * 22 * env;
            rotate -= 5 * env;
            break;
          case 'shrug':
            squash -= 0.06 * env;
            browBoost = 0.7 * env;
            earWiggle -= 12 * env;
            break;
          case 'wink':
            rotate += 6 * env;
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
            // Two big hops, ears flung up.
            const hop = Math.abs(Math.sin(u * Math.PI * 2));
            dy -= hop * 16 * env * motion;
            squash -= (hop - 0.5) * 0.06 * env * motion;
            earWiggle += Math.sin(u * Math.PI * 4) * 6 * env * motion;
            break;
          }
          case 'laugh': {
            // Quick bouncing chuckles, the head rocking back a little, ears flapping.
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

    // Only the head moves; the neck stays put and the head pivots on top of it.
    p.head.setAttribute('transform',
      `translate(0 ${f(dy)}) rotate(${f(rotate)} 0 ${NECK_PIVOT_Y}) scale(${f(1 - squash)} ${f(1 + squash)})`);

    // Ears: perk towards upright, droop outward and down. Mirrored for the right ear.
    const lift = face.earLift;
    const earAngle = lift >= 0 ? 14 - lift * 10 : 14 - lift * 62;
    p.ears.forEach((ear, i) => {
      const side = i === 0 ? -1 : 1;
      const angle = side * (earAngle + earWiggle * (i === 0 ? 1 : 0.6));
      ear.setAttribute('transform', `translate(${side * EAR_X} ${EAR_Y}) rotate(${f(angle)})`);
    });

    p.tuft.setAttribute('transform', `translate(0 ${f(-Math.max(0, -dy) * 0.4)})`);

    // Eyes: the felt lid comes down over the button as the eye closes; when smiling, the button
    // gives way to an embroidered ^.
    const open = Math.max(0, face.eyeOpen) * blink;
    const smile = Math.min(1, Math.max(0, face.eyeSmile));
    const gx = face.gazeX * 2.5;
    const gy = face.gazeY * 2;
    const wink = this.gesture?.name === 'wink'
      ? Math.sin(Math.PI * Math.min(1, (now - this.gesture.start) / GESTURE_MS.wink)) ** 0.5
      : 0;
    p.eyes.forEach((eye, i) => {
      const cx = (i === 0 ? -EYE_X : EYE_X);
      const scale = face.eyeOpen > 1 ? 1 + (face.eyeOpen - 1) * 0.35 : 1;
      eye.button.setAttribute('transform', `translate(${f(gx)} ${f(gy)}) translate(${cx} ${EYE_Y}) scale(${f(scale)}) translate(${-cx} ${-EYE_Y})`);
      eye.button.setAttribute('opacity', f(1 - smile));
      const eyeOpen = i === 1 ? open * (1 - wink) : open;
      const covered = Math.min(1, Math.max(0, 1 - eyeOpen)) * (EYE_R * 2 + 1);
      eye.lid.setAttribute('y', f(EYE_Y - EYE_R - 1 - (EYE_R * 2 + 2) + covered));
      eye.happy.setAttribute('opacity', f(smile));
    });

    // Embroidered brows: the inner end moves with tilt.
    const by = -24 - (face.browRaise + browBoost) * 6 + (face.eyeOpen < 0.5 ? 3 : 0);
    p.brows.forEach((brow, i) => {
      const side = i === 0 ? -1 : 1;
      brow.setAttribute('x1', f(side * 12 + gx * 0.4));
      brow.setAttribute('y1', f(by + face.browTilt * 5));
      brow.setAttribute('x2', f(side * 31 + gx * 0.4));
      brow.setAttribute('y2', f(by - face.browTilt * 2.5));
    });

    // Stitched mouth under the nose: corners lift with a smile; opening drops the lower lip.
    const mw = 12 * face.mouthWidth;
    const corner = MOUTH_Y - face.mouthCurve * 4;
    const upper = MOUTH_Y + face.mouthCurve * 6;
    const lower = upper + face.mouthOpen * 16;
    p.mouth.setAttribute('d', `M${f(-mw)},${f(corner)} Q0,${f(upper)} ${f(mw)},${f(corner)} Q0,${f(lower)} ${f(-mw)},${f(corner)} Z`);

    // Plush cheeks are always a little rosy.
    p.cheeks.setAttribute('opacity', f(0.3 + face.blush * 0.5));
    p.tear.setAttribute('opacity', f(face.tear));
    p.tear.setAttribute('transform', `translate(${f(EYE_X + 6 + gx)} ${f(EYE_Y + 14 + ((t * 12) % 16) * face.tear)})`);

    const thinking = this.state === 'thinking';
    p.thinking.setAttribute('opacity', thinking ? '1' : '0');
    if (thinking) {
      Array.from(p.thinking.children).forEach((dot, i) =>
        dot.setAttribute('opacity', f(0.25 + 0.75 * Math.max(0, Math.sin(t * 4 - i * 0.9)))));
    }

    p.hoof.setAttribute('opacity', hoofLift === null ? '0' : '1');
    p.hoof.setAttribute('transform', `translate(86 ${f(215 - (hoofLift ?? 0) * 90)}) rotate(${f(hoofAngle)} 0 30)`);

    const resting = this.state === 'resting';
    p.sleep.setAttribute('visibility', resting ? 'visible' : 'hidden');
    p.sleep.setAttribute('opacity', resting ? f(0.5 + 0.5 * Math.sin(t * 2)) : '0');
    p.sleep.setAttribute('transform', `translate(0 ${f(resting ? -((t * 6) % 12) : 0)})`);
  }
}
