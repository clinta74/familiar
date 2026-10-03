import { brighten, lightness, mix, mixVivid } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import { el, f, nextId } from './plush/parts';
import { emotions, type Emotion } from './types';

/** The spot has no body to sit on, so it keeps the frame the plush characters' heads had. */
const VIEWBOX = '-122 -182 244 330';

/** How the spot's outline is shaped at one moment. Everything is relative to its radius. */
export interface SpotShape {
  /** Overall size, 1 normal. */
  size: number;
  /** Width and height, 1 round. */
  sx: number;
  sy: number;
  /** Slow, soft lobes (low harmonics), as a fraction of the radius, and how fast they drift. */
  wobble: number;
  speed: number;
  /** Sharp points all round: anger. */
  spikes: number;
  /** Sagging: heavy at the bottom, flat on top. */
  sag: number;
  /** One side bulging out (a wave), and towards which angle. */
  lobe: number;
  lobeAngle: number;
  /** Fast shiver: laughing, speaking, excitement. */
  jiggle: number;
}

/** What the surface looks like: rough or smooth, glossy or matte, glowing or dim. */
export interface SpotSurface {
  /** How much the edge frays, as displacement. */
  roughness: number;
  /** The shine on top. */
  gloss: number;
  /** The halo around it. */
  glow: number;
}

/** Each emotion at full strength; the spot blends between them by mood. */
const SHAPES: Record<Emotion, SpotShape & SpotSurface & { tint: string | null; tintAmount: number }> = {
  neutral: { size: 1, sx: 1, sy: 1, wobble: 0.035, speed: 0.6, spikes: 0, sag: 0, lobe: 0, lobeAngle: 0, jiggle: 0, roughness: 1, gloss: 0.5, glow: 0.3, tint: null, tintAmount: 0 },
  // Happy doesn't change hue: tinting towards yellow sends a violet spot through pink and red,
  // which reads as cross. It brightens instead, and lights up warm from inside (spotCore, spotGlow).
  joy: { size: 1.07, sx: 1.03, sy: 1.0, wobble: 0.06, speed: 1.6, spikes: 0, sag: 0, lobe: 0, lobeAngle: 0, jiggle: 0.01, roughness: 0.5, gloss: 0.9, glow: 0.9, tint: null, tintAmount: 0 },
  sad: { size: 0.9, sx: 1.12, sy: 0.84, wobble: 0.02, speed: 0.25, spikes: 0, sag: 0.16, lobe: 0, lobeAngle: 0, jiggle: 0, roughness: 0.6, gloss: 0.15, glow: 0.1, tint: '#4f74b8', tintAmount: 0.6 },
  angry: { size: 1.02, sx: 1, sy: 1, wobble: 0.03, speed: 2.4, spikes: 0.16, sag: 0, lobe: 0, lobeAngle: 0, jiggle: 0.015, roughness: 7, gloss: 0.3, glow: 0.6, tint: '#e0413f', tintAmount: 0.65 },
  surprised: { size: 1.16, sx: 0.96, sy: 1.08, wobble: 0.01, speed: 1.0, spikes: 0.03, sag: 0, lobe: 0, lobeAngle: 0, jiggle: 0.005, roughness: 1.5, gloss: 1, glow: 1, tint: '#ffffff', tintAmount: 0.35 },
  relaxed: { size: 1, sx: 1.06, sy: 0.94, wobble: 0.025, speed: 0.3, spikes: 0, sag: 0.03, lobe: 0, lobeAngle: 0, jiggle: 0, roughness: 0, gloss: 0.6, glow: 0.45, tint: '#7ccfb5', tintAmount: 0.45 },
  thinking: { size: 0.97, sx: 1, sy: 1.02, wobble: 0.07, speed: 0.9, spikes: 0, sag: 0, lobe: 0, lobeAngle: 0, jiggle: 0, roughness: 2.5, gloss: 0.4, glow: 0.5, tint: '#9a7ae0', tintAmount: 0.5 },
};

const RADIUS = 72;
const POINTS = 96;

/**
 * The outline as a smooth closed path. Pure, so it can be tested: the same shape and time give the
 * same path. Points are joined with a Catmull-Rom spline, so spikes stay sharp-ish but nothing kinks.
 */
export function spotPath(shape: SpotShape, t: number, swirl = 0): string {
  const pts: [number, number][] = [];
  for (let i = 0; i < POINTS; i++) {
    const a = (i / POINTS) * Math.PI * 2;
    let r = 1
      + shape.wobble * (Math.sin(2 * a + t * shape.speed + swirl) * 0.6 + Math.sin(3 * a - t * shape.speed * 0.7 + 1.3 + swirl) * 0.4)
      + shape.spikes * Math.max(0, Math.sin(13 * a + t * 0.8)) ** 3
      + shape.jiggle * Math.sin(7 * a + t * 31)
      + shape.lobe * Math.max(0, Math.cos(a - shape.lobeAngle)) ** 6;
    // Sag: SVG y points down, so sin(a) > 0 is the bottom half - heavier there, flatter on top.
    r += shape.sag * (Math.sin(a) > 0 ? Math.sin(a) * 0.6 : Math.sin(a) * 0.9);
    const x = Math.cos(a) * r * RADIUS * shape.size * shape.sx;
    const y = Math.sin(a) * r * RADIUS * shape.size * shape.sy + shape.sag * RADIUS * 0.5;
    pts.push([x, y]);
  }

  let d = `M${f(pts[0]![0])},${f(pts[0]![1])}`;
  for (let i = 0; i < POINTS; i++) {
    const p0 = pts[(i - 1 + POINTS) % POINTS]!;
    const p1 = pts[i]!;
    const p2 = pts[(i + 1) % POINTS]!;
    const p3 = pts[(i + 2) % POINTS]!;
    const c1 = [p1[0] + (p2[0] - p0[0]) / 6, p1[1] + (p2[1] - p0[1]) / 6];
    const c2 = [p2[0] - (p3[0] - p1[0]) / 6, p2[1] - (p3[1] - p1[1]) / 6];
    d += ` C${f(c1[0]!)},${f(c1[1]!)} ${f(c2[0]!)},${f(c2[1]!)} ${f(p2[0])},${f(p2[1])}`;
  }
  return `${d} Z`;
}

/** Warm light, for a happy spot's core and glow. */
const WARM_LIGHT = '#fff1c4';
const WARM_GLOW = '#ffcf56';

/**
 * The colour for a mood: the user's colour, tinted towards each emotion as much as it's felt
 * (blended in OKLCH, so it stays vivid), and brighter when happy.
 */
export function spotColour(base: string, mood: Record<Emotion, number>): string {
  let colour = base;
  for (const e of emotions) {
    const { tint, tintAmount } = SHAPES[e];
    if (tint && mood[e] > 0.01) colour = mixVivid(colour, tint, Math.min(1, tintAmount * mood[e]));
  }
  return mood.joy > 0.01 ? brighten(colour, mood.joy) : colour;
}

/** The lit centre of the body: paler than the body, and glowing warm when happy. */
export function spotCore(body: string, joy: number, shine: number): string {
  return mix(mix(body, '#ffffff', shine), WARM_LIGHT, Math.min(1, 0.8 * joy));
}

/** The halo: the user's glow colour, turning golden when happy. */
export function spotGlow(accent: string, mood: Record<Emotion, number>): string {
  return mix(spotColour(accent, { ...mood, joy: 0 }), WARM_GLOW, Math.min(1, 0.75 * mood.joy));
}

/** The mood-weighted blend of the emotions' shapes and surfaces. */
export function blendShape(mood: Record<Emotion, number>): SpotShape & SpotSurface {
  const total = emotions.reduce((sum, e) => sum + mood[e], 0) || 1;
  const out = { size: 0, sx: 0, sy: 0, wobble: 0, speed: 0, spikes: 0, sag: 0, lobe: 0, lobeAngle: 0, jiggle: 0, roughness: 0, gloss: 0, glow: 0 };
  for (const e of emotions) {
    const w = mood[e] / total;
    const s = SHAPES[e];
    for (const key of Object.keys(out) as (keyof typeof out)[]) out[key] += s[key] * w;
  }
  return out;
}

/**
 * A spot of colour with no face (PLAN.md §5.5): it stays where it is and shows how it feels with its
 * shape, its surface and its colour. Happy, it's bright, warm, glossy and bouncy; sad, it sags, goes
 * blue and dull and small; cross, it turns red and spiky with a frayed edge; surprised, it swells,
 * round and shining; relaxed, it settles soft and smooth and green; thinking, its lobes drift slowly
 * round in violet. Speaking makes it pulse with the voice.
 */
export class SpotAvatar extends PlushCharacter {
  protected readonly root: SVGSVGElement;
  private readonly body: SVGPathElement;
  private readonly halo: SVGPathElement;
  private readonly sheen: SVGEllipseElement;
  private readonly displacement: SVGFEDisplacementMapElement;
  private readonly stops: [SVGStopElement, SVGStopElement];
  private readonly sparkles: SVGGElement;
  private base = '#7c5cff';
  private accent = '#ffcf56';
  private swirl = 0;
  private lastT = 0;

  constructor(container: HTMLElement, look?: PlushLook) {
    super();
    const id = nextId('spot');
    this.root = el('svg', { viewBox: VIEWBOX, class: 'spot', role: 'img', 'aria-label': 'Your familiar, a spot of colour' });
    const defs = el('defs', {}, this.root);

    const gradient = el('radialGradient', { id: `${id}-fill`, cx: '38%', cy: '32%', r: '75%' }, defs);
    this.stops = [el('stop', { offset: '0%' }, gradient), el('stop', { offset: '100%' }, gradient)];

    // The surface: noise displacing the edge, from glassy-smooth to frayed.
    const rough = el('filter', { id: `${id}-rough`, x: '-20%', y: '-20%', width: '140%', height: '140%' }, defs);
    el('feTurbulence', { type: 'fractalNoise', baseFrequency: 0.035, numOctaves: 2, seed: 3, result: 'noise' }, rough);
    this.displacement = el('feDisplacementMap', { in: 'SourceGraphic', in2: 'noise', scale: 1, xChannelSelector: 'R', yChannelSelector: 'G' }, rough);
    const blur = el('filter', { id: `${id}-glow`, x: '-50%', y: '-50%', width: '200%', height: '200%' }, defs);
    el('feGaussianBlur', { stdDeviation: 14 }, blur);

    // Centred a little high, so it sits in the stage where the plush characters' heads do.
    const g = el('g', { transform: 'translate(0 -10)' }, this.root);
    this.halo = el('path', { filter: `url(#${id}-glow)` }, g);
    this.body = el('path', { fill: `url(#${id}-fill)`, filter: `url(#${id}-rough)` }, g);
    this.sheen = el('ellipse', { class: 'spot-sheen', rx: 24, ry: 13 }, g);
    this.sparkles = el('g', { class: 'spot-sparkles' }, g);
    for (const [x, y] of [[-70, -60], [74, -44], [-56, 62], [66, 58], [0, -96]] as const) {
      el('path', { d: `M${x},${y - 7} L${x + 2},${y - 2} L${x + 7},${y} L${x + 2},${y + 2} L${x},${y + 7} L${x - 2},${y + 2} L${x - 7},${y} L${x - 2},${y - 2} Z` }, this.sparkles);
    }

    container.appendChild(this.root);
    if (look) this.setLook(look);
    this.start();
  }

  /** Its colour is the "wool" colour; the accessory colour is its glow. It wears nothing. */
  setLook(look: PlushLook): void {
    this.base = look.colours.wool;
    this.accent = look.colours.accent;
  }

  protected draw(pose: Pose): void {
    const shape = blendShape(pose.mood);
    const dt = Math.max(0, Math.min(0.1, pose.t - this.lastT));
    this.lastT = pose.t;

    // Thinking turns the lobes slowly round, like turning something over.
    this.swirl += dt * (0.2 + pose.mood.thinking * 1.2) * pose.motion;

    // Speaking: it swells and shivers with the voice.
    shape.size *= 1 + pose.voice * 0.07;
    shape.jiggle += pose.voice * 0.02;

    // Listening: it brightens towards you. Resting: it dims and slows right down.
    if (pose.state === 'listening') shape.glow += 0.3;
    if (pose.state === 'resting') {
      shape.glow *= 0.3;
      shape.speed *= 0.3;
      shape.size *= 0.95;
    }

    // Breathing: a slow swell.
    const breath = Math.sin(pose.t * Math.PI * 2 / 3.8) * 0.015 * pose.motion;
    shape.sx *= 1 + breath;
    shape.sy *= 1 + breath;

    // Gestures, done in place with shape alone.
    let flash = 0;
    if (pose.gesture) {
      const { name, u } = pose.gesture;
      const env = Math.sin(Math.PI * u);
      switch (name) {
        case 'nod':
          shape.sy *= 1 - Math.max(0, Math.sin(u * Math.PI * 4)) * 0.1 * pose.motion;
          break;
        case 'shake':
          shape.sx *= 1 + Math.sin(u * Math.PI * 6) * 0.08 * env * pose.motion;
          break;
        case 'tilt':
          shape.lobe += 0.12 * env;
          shape.lobeAngle = -Math.PI / 4;
          break;
        case 'wave':
          // A pseudopod reaching out to the side and back, twice.
          shape.lobe += 0.3 * Math.abs(Math.sin(u * Math.PI * 2)) * env;
          shape.lobeAngle = -0.35;
          break;
        case 'shrug':
          shape.sy *= 1 - 0.1 * env;
          shape.sx *= 1 + 0.06 * env;
          break;
        case 'laugh':
          shape.jiggle += 0.05 * env * pose.motion;
          flash = 0.4 * env;
          break;
        case 'wink':
          shape.lobe += 0.08 * env;
          shape.lobeAngle = Math.PI * 0.15;
          flash = 0.5 * env;
          break;
        case 'sigh':
          shape.size *= 1 - 0.1 * env;
          shape.sag += 0.08 * env;
          break;
        case 'yawn':
          shape.sy *= 1 + 0.18 * env;
          shape.sx *= 1 - 0.08 * env;
          break;
        case 'excited':
          shape.size *= 1 + Math.abs(Math.sin(u * Math.PI * 4)) * 0.1 * env * pose.motion;
          flash = 0.7 * env;
          break;
      }
    }

    // Colour: the user's colour tinted by mood, lit from the top left, darker at the rim; the
    // halo in their accent colour, tinted the same way.
    const colour = spotColour(this.base, pose.mood);
    const dim = pose.state === 'resting' ? 0.35 : 0;
    const centre = mix(spotCore(colour, pose.mood.joy, 0.35 + shape.gloss * 0.2 + flash * 0.3), '#202028', dim);
    const rim = mix(mix(colour, lightness(colour) > 0.5 ? '#3a3040' : '#000000', 0.25), '#202028', dim);
    this.stops[0].setAttribute('stop-color', centre);
    this.stops[1].setAttribute('stop-color', rim);

    const path = spotPath(shape, pose.t, this.swirl);
    this.body.setAttribute('d', path);
    this.halo.setAttribute('d', path);
    this.halo.setAttribute('fill', spotGlow(this.accent, pose.mood));
    this.halo.setAttribute('opacity', f(Math.min(1, shape.glow * 0.7 + flash * 0.3)));
    this.displacement.setAttribute('scale', f(shape.roughness * (pose.motion < 1 ? 0.5 : 1)));

    // The shine: big and bright when glossy (happy, surprised), small and faint when matte (sad).
    const scale = shape.size * (0.7 + shape.gloss * 0.5);
    this.sheen.setAttribute('transform', `translate(${f(-24 * shape.size * shape.sx)} ${f(-30 * shape.size * shape.sy + shape.sag * 20)}) rotate(-25) scale(${f(scale)})`);
    this.sheen.setAttribute('opacity', f(0.15 + shape.gloss * 0.45 + flash * 0.2));

    // Sparkles come out when it's delighted or excited.
    const sparkle = Math.min(1, pose.mood.joy * 0.6 + pose.mood.surprised * 0.4 + flash);
    this.sparkles.setAttribute('opacity', f(sparkle));
    Array.from(this.sparkles.children).forEach((s, i) => {
      const twinkle = 0.5 + 0.5 * Math.sin(pose.t * 5 + i * 1.7);
      s.setAttribute('opacity', f(twinkle));
      s.setAttribute('fill', mix(this.accent, '#ffffff', 0.5));
    });
  }
}
