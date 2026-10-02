import type { Accessory } from '../appearance';
import type { FaceParams } from '../faceExpressions';
import type { Pose } from './engine';

// Building blocks shared by the plush characters: fleece, button eyes, stitched features,
// accessories. Every character is drawn in the same viewBox, head centred on the origin.

export const SVG = 'http://www.w3.org/2000/svg';
export const VIEWBOX = '-122 -182 244 330';

let instances = 0;

/** A unique prefix for ids (clip paths, filters) inside one character's SVG. */
export const nextId = (kind: string) => `${kind}-${++instances}`;

export const f = (n: number) => n.toFixed(2);

export function el<K extends keyof SVGElementTagNameMap>(name: K, attrs: Record<string, string | number> = {}, parent?: Element) {
  const node = document.createElementNS(SVG, name);
  for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, String(value));
  parent?.appendChild(node);
  return node;
}

/** The root SVG with the fabric filter: a little noise displaces edges so they read as plush, not vector. */
export function plushRoot(kind: string, label: string): { root: SVGSVGElement; defs: SVGDefsElement; id: string } {
  const id = nextId('plush');
  // plush-llama is the shared sizing class (the llama came first); plush-<kind> is for per-character CSS.
  const root = el('svg', { viewBox: VIEWBOX, class: `plush-llama plush-${kind}`, role: 'img', 'aria-label': label });
  const defs = el('defs', {}, root);
  const fuzz = el('filter', { id: `${id}-fuzz`, x: '-10%', y: '-10%', width: '120%', height: '120%' }, defs);
  el('feTurbulence', { type: 'fractalNoise', baseFrequency: 0.8, numOctaves: 2, seed: 7, result: 'noise' }, fuzz);
  el('feDisplacementMap', { in: 'SourceGraphic', in2: 'noise', scale: 3, xChannelSelector: 'R', yChannelSelector: 'G' }, fuzz);
  root.style.setProperty('--plush-fuzz', `url(#${id}-fuzz)`);
  return { root, defs, id };
}

export type Puff = [x: number, y: number, r: number];

/**
 * A cloud of fleece or fur: overlapping puffs, drawn twice - slightly larger in the shadow colour
 * first, then in the coat colour - so only the outer edge of the cloud gets an outline.
 */
export function wool(parent: SVGGElement, puffs: Puff[], className = ''): SVGGElement {
  const g = el('g', { class: `plush-fuzzy ${className}`.trim() }, parent);
  for (const [cx, cy, r] of puffs) el('circle', { cx, cy, r: r + 2.6, class: 'plush-wool-outline' }, g);
  for (const [cx, cy, r] of puffs) el('circle', { cx, cy, r, class: 'plush-wool' }, g);
  return g;
}

/** Little "c" curls stitched into fleece. */
export function curls(parent: SVGGElement, at: [x: number, y: number, flip?: boolean][]) {
  const d = at.map(([x, y, flip]) => (flip
    ? `M${x + 5},${y} A5,5 0 1 0 ${x - 3},${y + 4}`
    : `M${x - 5},${y} A5,5 0 1 1 ${x + 3},${y + 4}`)).join(' ');
  el('path', { class: 'plush-curl', d }, parent);
}

/** Where a character's face sits. Everything is mirrored about x = 0. */
export interface FaceLayout {
  eyeX: number;
  eyeY: number;
  eyeR: number;
  /** Brow height when relaxed; brows run from innerX to outerX. */
  browY: number;
  browInnerX: number;
  browOuterX: number;
  mouthY: number;
  /** Half-width of the mouth at its normal width. */
  mouthW: number;
  /** How far the lower lip drops when wide open. */
  mouthDrop: number;
  cheekX: number;
  cheekY: number;
  /** Button eyes (felt lid over a button) or googly eyes (white with a pupil that looks about). */
  eyeStyle: 'button' | 'googly';
  lashes: boolean;
}

interface Eye {
  button: SVGGElement;
  lid: SVGRectElement;
  happy: SVGPathElement;
  pupil?: SVGCircleElement;
}

export interface FaceParts {
  layout: FaceLayout;
  eyes: [Eye, Eye];
  brows: [SVGLineElement, SVGLineElement];
  mouth: SVGPathElement;
  teeth?: SVGPathElement;
  cheeks: SVGGElement;
  tear: SVGPathElement;
}

function eye(parent: SVGGElement, layout: FaceLayout, cx: number, clipId: string): Eye {
  const { eyeY: cy, eyeR: r } = layout;
  const clip = el('clipPath', { id: clipId }, parent);
  el('circle', { cx, cy, r: r + 0.5 }, clip);

  const button = el('g', {}, parent);
  let pupil: SVGCircleElement | undefined;
  if (layout.eyeStyle === 'googly') {
    el('circle', { cx, cy, r, class: 'plush-sclera' }, button);
    pupil = el('circle', { cx, cy, r: r * 0.45, class: 'plush-button' }, button);
    el('circle', { cx: cx + r * 0.2, cy: cy - r * 0.25, r: r * 0.14, class: 'plush-button-shine' }, button);
  } else {
    el('circle', { cx, cy, r, class: 'plush-button' }, button);
    el('circle', { cx: cx + r * 0.29, cy: cy - r * 0.32, r: r * 0.27, class: 'plush-button-shine' }, button);
    el('circle', { cx: cx - r * 0.32, cy: cy + r * 0.34, r: r * 0.11, class: 'plush-button-shine' }, button);
  }

  // A felt lid that slides down over the eye: closing, sleepy, cross.
  const lidGroup = el('g', { 'clip-path': `url(#${clipId})` }, button);
  const lid = el('rect', { x: cx - r - 1, width: r * 2 + 2, height: r * 2 + 2, class: 'plush-lid' }, lidGroup);

  // Embroidered ^ for a happy, eyes-closed smile.
  const happy = el('path', { class: 'plush-thread', d: `M${cx - r * 0.8},${cy + 3} Q${cx},${cy - r * 0.8} ${cx + r * 0.8},${cy + 3}` }, parent);

  if (layout.lashes) {
    const side = Math.sign(cx);
    const x = (dx: number) => cx + side * dx;
    el('path', {
      class: 'plush-lash',
      d: `M${x(7)},${cy - 8} L${x(11)},${cy - 14} M${x(10)},${cy - 4} L${x(16)},${cy - 8} M${x(11.5)},${cy + 1} L${x(17.5)},${cy - 1}`,
    }, parent);
  }

  return { button, lid, happy, pupil };
}

/**
 * Eyes, brows, cheeks, a stitched mouth and a tear, laid out on `head`. `beforeMouth` draws what
 * goes between the eyes and the mouth (a snout and nose), so the mouth lands on top of it.
 */
export function buildFace(
  head: SVGGElement,
  layout: FaceLayout,
  id: string,
  options: { teeth?: boolean; beforeMouth?: (head: SVGGElement) => void } = {},
): FaceParts {
  const cheeks = el('g', { class: 'plush-cheeks' }, head);
  el('ellipse', { cx: -layout.cheekX, cy: layout.cheekY, rx: 9.5, ry: 6.5 }, cheeks);
  el('ellipse', { cx: layout.cheekX, cy: layout.cheekY, rx: 9.5, ry: 6.5 }, cheeks);

  const eyes: [Eye, Eye] = [eye(head, layout, -layout.eyeX, `${id}-eye-l`), eye(head, layout, layout.eyeX, `${id}-eye-r`)];
  const brows: [SVGLineElement, SVGLineElement] = [el('line', { class: 'plush-thread' }, head), el('line', { class: 'plush-thread' }, head)];
  options.beforeMouth?.(head);
  const mouth = el('path', { class: 'plush-mouth' }, head);
  const teeth = options.teeth ? el('path', { class: 'plush-teeth' }, head) : undefined;
  const tear = el('path', { class: 'plush-tear', d: 'M0,-7 C4,-1 6,3 6,6 A6,6 0 0 1 -6,6 C-6,3 -4,-1 0,-7 Z' }, head);
  return { layout, eyes, brows, mouth, teeth, cheeks, tear };
}

/** Moves the face to match the face parameters and the pose. */
export function drawFace(parts: FaceParts, face: FaceParams, pose: Pose) {
  const { layout: L } = parts;
  const open = Math.max(0, face.eyeOpen) * pose.blink;
  const smile = Math.min(1, Math.max(0, face.eyeSmile));
  const gx = face.gazeX * 2.5;
  const gy = face.gazeY * 2;

  parts.eyes.forEach((e, i) => {
    const cx = i === 0 ? -L.eyeX : L.eyeX;
    const scale = face.eyeOpen > 1 ? 1 + (face.eyeOpen - 1) * 0.35 : 1;
    if (e.pupil) {
      // Googly: the eye stays, the pupil looks about.
      e.button.setAttribute('transform', `translate(${cx} ${L.eyeY}) scale(${f(scale)}) translate(${-cx} ${-L.eyeY})`);
      e.pupil.setAttribute('cx', f(cx + face.gazeX * L.eyeR * 0.4));
      e.pupil.setAttribute('cy', f(L.eyeY + face.gazeY * L.eyeR * 0.35));
    } else {
      e.button.setAttribute('transform', `translate(${f(gx)} ${f(gy)}) translate(${cx} ${L.eyeY}) scale(${f(scale)}) translate(${-cx} ${-L.eyeY})`);
    }
    e.button.setAttribute('opacity', f(1 - smile));
    const eyeOpen = i === 1 ? open * (1 - pose.wink) : open;
    const covered = Math.min(1, Math.max(0, 1 - eyeOpen)) * (L.eyeR * 2 + 1);
    e.lid.setAttribute('y', f(L.eyeY - L.eyeR - 1 - (L.eyeR * 2 + 2) + covered));
    e.happy.setAttribute('opacity', f(smile));
  });

  // Embroidered brows: the inner end moves with tilt.
  const by = L.browY - (face.browRaise + pose.browBoost) * 6 + (face.eyeOpen < 0.5 ? 3 : 0);
  parts.brows.forEach((brow, i) => {
    const side = i === 0 ? -1 : 1;
    brow.setAttribute('x1', f(side * L.browInnerX + gx * 0.4));
    brow.setAttribute('y1', f(by + face.browTilt * 5));
    brow.setAttribute('x2', f(side * L.browOuterX + gx * 0.4));
    brow.setAttribute('y2', f(by - face.browTilt * 2.5));
  });

  // Stitched mouth: corners lift with a smile; opening drops the lower lip.
  const mw = L.mouthW * face.mouthWidth;
  const corner = L.mouthY - face.mouthCurve * 4;
  const upper = L.mouthY + face.mouthCurve * 6;
  const lower = upper + face.mouthOpen * L.mouthDrop;
  parts.mouth.setAttribute('d', `M${f(-mw)},${f(corner)} Q0,${f(upper)} ${f(mw)},${f(corner)} Q0,${f(lower)} ${f(-mw)},${f(corner)} Z`);

  // Two little fangs hanging from the upper lip.
  if (parts.teeth) {
    const tx = mw * 0.45;
    const ty = (corner + upper) / 2 + 1;
    parts.teeth.setAttribute('d', `M${f(-tx - 4)},${f(ty)} L${f(-tx)},${f(ty + 7)} L${f(-tx + 4)},${f(ty)} Z M${f(tx - 4)},${f(ty)} L${f(tx)},${f(ty + 7)} L${f(tx + 4)},${f(ty)} Z`);
  }

  // Plush cheeks are always a little rosy.
  parts.cheeks.setAttribute('opacity', f(0.3 + face.blush * 0.5));
  parts.tear.setAttribute('opacity', f(face.tear));
  parts.tear.setAttribute('transform', `translate(${f(L.eyeX + 6 + gx)} ${f(L.eyeY + 14 + ((pose.t * 12) % 16) * face.tear)})`);
}

/** Thought bubbles over the head, and a "z" for resting. */
export interface Overlay {
  thinking: SVGGElement;
  sleep: SVGTextElement;
}

export function buildOverlay(root: SVGSVGElement): Overlay {
  const thinking = el('g', { class: 'plush-thinking' }, root);
  [[72, -128, 5], [88, -146, 7], [106, -166, 9]].forEach(([cx, cy, r]) => el('circle', { cx: cx!, cy: cy!, r: r! }, thinking));
  const sleep = el('text', { x: 68, y: -112, class: 'plush-sleep', 'aria-hidden': 'true' }, root);
  sleep.textContent = 'z';
  return { thinking, sleep };
}

export function drawOverlay(overlay: Overlay, pose: Pose) {
  const thinking = pose.state === 'thinking';
  overlay.thinking.setAttribute('opacity', thinking ? '1' : '0');
  if (thinking) {
    Array.from(overlay.thinking.children).forEach((dot, i) =>
      dot.setAttribute('opacity', f(0.25 + 0.75 * Math.max(0, Math.sin(pose.t * 4 - i * 0.9)))));
  }

  const resting = pose.state === 'resting';
  overlay.sleep.setAttribute('visibility', resting ? 'visible' : 'hidden');
  overlay.sleep.setAttribute('opacity', resting ? f(0.5 + 0.5 * Math.sin(pose.t * 2)) : '0');
  overlay.sleep.setAttribute('transform', `translate(0 ${f(resting ? -((pose.t * 6) % 12) : 0)})`);
}

/** A paw (or hoof) that pops up from below the stage to wave. */
export function buildPaw(root: SVGSVGElement, kind: 'hoof' | 'paw' | 'claw'): SVGGElement {
  const g = el('g', {}, root);
  wool(g, [[0, 40, 15], [0, 18, 15], [0, 62, 15]]);
  if (kind === 'hoof') {
    el('path', { class: 'plush-hoof', d: 'M-12,2 C-12,-10 -6,-14 0,-14 C6,-14 12,-10 12,2 C6,-2 -6,-2 -12,2 Z' }, g);
    el('path', { class: 'plush-hoof-split', d: 'M0,-14 L0,-4' }, g);
  } else {
    // A round paw with toe beans; the monster's has little claws.
    el('ellipse', { class: 'plush-hoof', cx: 0, cy: -2, rx: 14, ry: 11 }, g);
    for (const x of [-8, 0, 8]) el('circle', { class: 'plush-bean', cx: x, cy: -9, r: 3 }, g);
    if (kind === 'claw') {
      el('path', { class: 'plush-claw', d: 'M-10,-14 L-8,-21 L-6,-14 M-2,-15 L0,-22 L2,-15 M6,-14 L8,-21 L10,-14' }, g);
    }
  }
  return g;
}

export function drawPaw(paw: SVGGElement, pose: Pose) {
  paw.setAttribute('opacity', pose.pawLift === null ? '0' : '1');
  paw.setAttribute('transform', `translate(86 ${f(215 - (pose.pawLift ?? 0) * 90)}) rotate(${f(pose.pawAngle)} 0 30)`);
}

/** Where a character wears each accessory. */
export interface AccessoryAnchors {
  /** The bow goes on this group (an ear, usually, so it moves with it), at this transform. */
  bow: { parent: SVGGElement; transform: string };
  /** Glasses sit over the eyes. */
  glasses: { parent: SVGGElement; layout: FaceLayout };
  flower: { parent: SVGGElement; x: number; y: number };
  /** The scarf wraps a neck that doesn't move with the head. */
  scarf: { parent: SVGGElement; y: number; halfWidth: number };
}

export type AccessoryParts = Record<Exclude<Accessory, 'none'>, SVGGElement>;

/** Every accessory, in the accent colour, drawn once and shown or hidden by setLook. */
export function buildAccessories(a: AccessoryAnchors): AccessoryParts {
  const bow = el('g', { class: 'plush-accessory', transform: a.bow.transform }, a.bow.parent);
  el('path', { class: 'plush-accent', d: 'M0,0 C-8,-12 -22,-12 -20,0 C-22,12 -8,12 0,0 Z' }, bow);
  el('path', { class: 'plush-accent', d: 'M0,0 C8,-12 22,-12 20,0 C22,12 8,12 0,0 Z' }, bow);
  el('circle', { class: 'plush-accent-knot', r: 4.5 }, bow);

  const { layout: L } = a.glasses;
  const glasses = el('g', { class: 'plush-accessory' }, a.glasses.parent);
  for (const x of [-L.eyeX, L.eyeX]) el('circle', { class: 'plush-glasses', cx: x, cy: L.eyeY, r: L.eyeR + 5 }, glasses);
  el('path', { class: 'plush-glasses', d: `M${-L.eyeX + L.eyeR + 5},${L.eyeY - 2} Q0,${L.eyeY - 9} ${L.eyeX - L.eyeR - 5},${L.eyeY - 2}` }, glasses);

  const flower = el('g', { class: 'plush-accessory', transform: `translate(${a.flower.x} ${a.flower.y})` }, a.flower.parent);
  for (let i = 0; i < 5; i++) {
    const angle = (i / 5) * Math.PI * 2;
    el('circle', { class: 'plush-accent', cx: Math.cos(angle) * 7, cy: Math.sin(angle) * 7, r: 6 }, flower);
  }
  el('circle', { class: 'plush-flower-centre', r: 4.5 }, flower);

  const { y, halfWidth: w } = a.scarf;
  const scarf = el('g', { class: 'plush-accessory' }, a.scarf.parent);
  el('path', { class: 'plush-accent', d: `M${-w},${y} C${-w / 2},${y + 14} ${w / 2},${y + 14} ${w},${y} L${w + 2},${y + 26} C${w / 2},${y + 40} ${-w / 2},${y + 40} ${-w - 2},${y + 26} Z` }, scarf);
  el('path', { class: 'plush-accent', d: `M${w * 0.4},${y + 22} L${w * 0.7},${y + 22} L${w * 0.8},${y + 78} L${w * 0.45},${y + 80} Z` }, scarf);
  el('path', { class: 'plush-accent-stripe', d: `M${-w + 2},${y + 13} C${-w / 2},${y + 26} ${w / 2},${y + 26} ${w - 2},${y + 13} M${w * 0.48},${y + 42} L${w * 0.76},${y + 41} M${w * 0.5},${y + 60} L${w * 0.78},${y + 59}` }, scarf);

  const parts = { bow, glasses, flower, scarf };
  for (const g of Object.values(parts)) g.style.display = 'none';
  return parts;
}

export function showAccessory(parts: AccessoryParts, accessory: Accessory) {
  for (const [name, g] of Object.entries(parts)) g.style.display = name === accessory ? '' : 'none';
}
