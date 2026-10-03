import type { Accessory } from '../appearance';
import type { FaceParams } from '../faceExpressions';
import type { Pose } from './engine';

// Building blocks shared by the plush characters: fleece, button eyes, stitched features, a
// sitting body, accessories. Every character is drawn in the same viewBox, head centred on the
// origin and its body sitting below, feet on the ground.

export const SVG = 'http://www.w3.org/2000/svg';
export const VIEWBOX = '-136 -182 272 420';
/** Where the sitting characters' feet rest. */
export const GROUND_Y = 222;

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

/** `top` is where the lowest bubble floats, above and to the right of the head. */
export function buildOverlay(root: SVGSVGElement, top = -128): Overlay {
  // Hidden until the first frame says otherwise.
  const thinking = el('g', { class: 'plush-thinking', opacity: 0 }, root);
  [[72, top, 5], [88, top - 18, 7], [106, top - 38, 9]].forEach(([cx, cy, r]) => el('circle', { cx: cx!, cy: cy!, r: r! }, thinking));
  const sleep = el('text', { x: 68, y: top + 16, class: 'plush-sleep', opacity: 0, 'aria-hidden': 'true' }, root);
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

export type PawKind = 'paw' | 'hoof' | 'claw';

/** A sitting character's body: where its limbs go and what kind of paws it has. */
export interface BodySpec {
  /** Draws the torso into the body group; the feet, head and arms go on over it. */
  torso: (body: SVGGElement) => void;
  /** Where the head pivots on the body for tilts and shakes (y, in head coordinates). */
  neckPivotY: number;
  /** Where each arm hangs from, mirrored about x = 0. */
  shoulder: [x: number, y: number];
  /** The arm, hanging straight down from the shoulder; fluffy ones get fleece puffs. */
  arm: { length: number; width: number; kind: PawKind; fluffy?: boolean };
  /** Where each hind foot rests, mirrored, toes towards the viewer. */
  foot: [x: number, y: number];
  /** The foot's size, and how far its toes turn out, in degrees. */
  feet: { rx: number; ry: number; kind: PawKind; splay?: number };
}

export interface BodyParts {
  /** The whole character bar its tail: it breathes, leans and bobs as one. */
  group: SVGGElement;
  /** The head goes here, over the torso and feet, and bobs and tilts on the neck. */
  head: SVGGElement;
  arms: [SVGGElement, SVGGElement];
  feet: [SVGGElement, SVGGElement];
  spec: BodySpec;
}

/** A hind foot, centred on the origin, toes pointing out to `side`: a soft pad with stitched toes. */
export function foot(parent: SVGGElement, { rx, ry, kind }: BodySpec['feet'], side: number): SVGGElement {
  const g = el('g', {}, parent);
  const toe = side * rx * 0.55;
  if (kind === 'hoof') {
    // A fleecy foot with a hoof at the toe.
    el('ellipse', { rx: rx + 2.6, ry: ry + 2.6, class: 'plush-wool-outline' }, g);
    el('ellipse', { rx, ry, class: 'plush-wool plush-fuzzy' }, g);
    el('ellipse', { cx: side * rx * 0.42, cy: 1, rx: rx * 0.5, ry: ry * 0.72, class: 'plush-hoof' }, g);
    el('path', { class: 'plush-hoof-split', d: `M${f(side * rx * 0.42)},${f(1 - ry * 0.55)} L${f(side * rx * 0.42)},${f(1 + ry * 0.25)}` }, g);
    return g;
  }
  el('ellipse', { rx, ry, class: 'plush-paw plush-fuzzy' }, g);
  el('path', { class: 'plush-toes', d: `M${f(toe + side * 7)},${f(-ry * 0.42)} l${f(-side * 7)},0 M${f(toe + side * 8)},${f(ry * 0.4)} l${f(-side * 7)},0` }, g);
  if (kind === 'claw') {
    const tip = side * (rx - 2);
    el('path', { class: 'plush-claw', d: `M${f(tip)},${f(-ry * 0.5)} l${f(side * 7)},-2 M${f(side * rx)},0 l${f(side * 8)},0 M${f(tip)},${f(ry * 0.5)} l${f(side * 7)},2` }, g);
  }
  return g;
}

/**
 * An arm hanging from the shoulder at the origin: a soft limb, a little narrower at the wrist,
 * sewn on with a rounded shoulder, ending in a paw. Rotate the group to raise it.
 */
function arm(parent: SVGGElement, { length, width, kind, fluffy }: BodySpec['arm'], side: number): SVGGElement {
  const g = el('g', {}, parent);
  const w = width / 2;
  el('path', {
    class: 'plush-wool plush-fuzzy plush-outlined',
    d: `M${f(-w)},0 C${f(-w - 2)},${f(length * 0.4)} ${f(-w * 0.8)},${f(length * 0.8)} ${f(-w * 0.75)},${f(length)} L${f(w * 0.75)},${f(length)} `
      + `C${f(w * 0.8)},${f(length * 0.8)} ${f(w + 2)},${f(length * 0.4)} ${f(w)},0 A${f(w)},${f(w)} 0 0 0 ${f(-w)},0 Z`,
  }, g);
  if (fluffy) wool(g, [[0, length * 0.22, w + 1], [side * 2, length * 0.52, w], [0, length * 0.8, w - 1]]);

  if (kind === 'hoof') {
    el('path', { class: 'plush-hoof', transform: `translate(0 ${f(length + 9)}) scale(${f(w / 11)})`, d: 'M-12,2 C-12,-10 -6,-14 0,-14 C6,-14 12,-10 12,2 C6,-2 -6,-2 -12,2 Z' }, g);
    el('path', { class: 'plush-hoof-split', d: `M0,${f(length - 4)} L0,${f(length + 5)}` }, g);
    return g;
  }
  const ry = w * 0.9;
  el('ellipse', { cy: length, rx: w * 1.25, ry, class: 'plush-paw plush-fuzzy' }, g);
  el('path', { class: 'plush-toes', d: `M${f(-w * 0.4)},${f(length + ry)} l0,-6 M${f(w * 0.4)},${f(length + ry)} l0,-6` }, g);
  if (kind === 'claw') {
    el('path', { class: 'plush-claw', d: `M${f(-w * 0.8)},${f(length + ry - 2)} l-2,7 M0,${f(length + ry)} l0,8 M${f(w * 0.8)},${f(length + ry - 2)} l2,7` }, g);
  }
  return g;
}

/** Builds a sitting body on the root: torso, feet, a group for the head, arms, in that order. */
export function buildBody(root: SVGSVGElement, spec: BodySpec): BodyParts {
  const group = el('g', {}, root);
  spec.torso(group);
  const feet: [SVGGElement, SVGGElement] = [foot(group, spec.feet, -1), foot(group, spec.feet, 1)];
  const head = el('g', {}, group);
  const arms: [SVGGElement, SVGGElement] = [arm(group, spec.arm, -1), arm(group, spec.arm, 1)];
  return { group, head, arms, feet, spec };
}

/**
 * Moves a sitting body with the pose. The body breathes (the chest filling as the head rises),
 * slumps when sad and leans a little into the head's tilt, with its feet on the ground; the head
 * does the rest of the tilt and bob on the neck. The right arm lifts to wave, both go out for a
 * shrug, and the feet kick up with each hop of joy.
 */
export function drawBody(body: BodyParts, pose: Pose) {
  const { spec } = body;
  const sad = pose.mood.sad;
  const sx = 1 - pose.breath * 0.006 + sad * 0.03;
  const sy = 1 + pose.breath * 0.008 - sad * 0.05;
  // The body moves only with its breathing, a sad slump and hops; nods, shakes and tilts are the
  // head's alone.
  const bodyDy = pose.breath * 1.5 - pose.hop * (pose.gesture?.name === 'excited' ? 16 : 5);
  body.group.setAttribute('transform',
    `translate(0 ${f(bodyDy)}) translate(0 ${GROUND_Y}) scale(${f(sx)} ${f(sy)}) translate(0 ${-GROUND_Y})`);
  body.head.setAttribute('transform',
    `translate(0 ${f(pose.dy - bodyDy)}) rotate(${f(pose.rotate)} 0 ${spec.neckPivotY}) scale(${f(1 - pose.squash)} ${f(1 + pose.squash)})`);

  const shrug = pose.gesture?.name === 'shrug' ? Math.sin(Math.PI * pose.gesture.u) * 30 : 0;
  const lift = pose.pawLift ?? 0;
  const [ax, ay] = spec.shoulder;
  body.arms[0].setAttribute('transform', `translate(${-ax} ${ay}) rotate(${f(shrug + pose.breath)})`);
  body.arms[1].setAttribute('transform', `translate(${ax} ${ay}) rotate(${f(-shrug - lift * 130 + pose.pawAngle * 0.7 - pose.breath)})`);

  const [fx, fy] = spec.foot;
  const splay = spec.feet.splay ?? 0;
  body.feet.forEach((foot, i) => {
    const side = i === 0 ? -1 : 1;
    foot.setAttribute('transform', `translate(${side * fx} ${fy}) rotate(${f(-side * (splay + pose.hop * 8))})`);
  });
}

/** Where a character wears each accessory. */
export interface AccessoryAnchors {
  /** The bow goes on this group (an ear, usually, so it moves with it), at this transform. */
  bow: { parent: SVGGElement; transform: string };
  /** Glasses sit over the eyes. */
  glasses: { parent: SVGGElement; layout: FaceLayout };
  flower: { parent: SVGGElement; x: number; y: number };
  /**
   * The scarf wraps the neck just under the chin: `y` is the top of the wrap at its middle, and
   * `halfWidth` how far it reaches each side. It belongs to the head (it moves with a nod), drawn
   * over the bottom of the head and the top of the body.
   */
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
  // The wrap: a soft band sagging a little at the front, its ends rounding away out of sight.
  el('path', {
    class: 'plush-accent',
    d: `M${-w},${y - 3} C${-w * 0.5},${y + 9} ${w * 0.5},${y + 9} ${w},${y - 3} C${w + 5},${y + 5} ${w + 3},${y + 14} ${w - 2},${y + 18} `
      + `C${w * 0.5},${y + 29} ${-w * 0.5},${y + 29} ${-w + 2},${y + 18} C${-w - 3},${y + 14} ${-w - 5},${y + 5} ${-w},${y - 3} Z`,
  }, scarf);
  el('path', { class: 'plush-accent-stripe', d: `M${-w + 7},${y + 7} C${-w * 0.5},${y + 17} ${w * 0.5},${y + 17} ${w - 7},${y + 7}` }, scarf);

  // A knot off to one side, with two tails: one hanging, one kicked out a little.
  const kx = w * 0.38;
  const ky = y + 14;
  el('path', { class: 'plush-accent', d: `M${kx - 7},${ky + 3} C${kx - 9},${ky + 18} ${kx - 10},${ky + 30} ${kx - 12},${ky + 42} L${kx + 2},${ky + 44} C${kx + 3},${ky + 31} ${kx + 4},${ky + 18} ${kx + 5},${ky + 4} Z` }, scarf);
  el('path', { class: 'plush-accent', d: `M${kx + 2},${ky + 3} C${kx + 9},${ky + 14} ${kx + 15},${ky + 25} ${kx + 21},${ky + 35} L${kx + 32},${ky + 29} C${kx + 23},${ky + 20} ${kx + 15},${ky + 10} ${kx + 9},${ky + 1} Z` }, scarf);
  el('path', { class: 'plush-accent-stripe', d: `M${kx - 9},${ky + 26} L${kx + 3},${ky + 27} M${kx + 13},${ky + 22} L${kx + 22},${ky + 16}` }, scarf);
  el('path', { class: 'plush-accent-fringe', d: `M${kx - 10},${ky + 43} l-1,5 M${kx - 5},${ky + 43} l0,5 M${kx},${ky + 44} l1,5 M${kx + 23},${ky + 34} l3,4 M${kx + 27},${ky + 32} l4,3 M${kx + 31},${ky + 30} l4,2` }, scarf);
  el('ellipse', { class: 'plush-accent', cx: kx, cy: ky, rx: 9, ry: 8 }, scarf);

  const parts = { bow, glasses, flower, scarf };
  for (const g of Object.values(parts)) g.style.display = 'none';
  return parts;
}

export function showAccessory(parts: AccessoryParts, accessory: Accessory) {
  for (const [name, g] of Object.entries(parts)) g.style.display = name === accessory ? '' : 'none';
}
