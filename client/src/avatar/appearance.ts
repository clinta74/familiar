// How a familiar looks (PLAN.md §5.5): the server's AppearanceView (the generated protocol,
// MOBILE.md §6), which it validates - colours are #rrggbb and the accessory is one of the known ones
// before anything here puts them into CSS.
import type { AppearanceView, LlamaColours } from '../protocol/generated';
import catalogue from './characters.json';

export type { LlamaColours };

/** How a familiar looks: a built-in character, coloured and dressed, or an uploaded VRM model. */
export type Appearance = AppearanceView;

export type Accessory = Appearance['accessory'];

export const accessories: readonly Accessory[] = ['none', 'bow', 'scarf', 'glasses', 'flower'];

/** The built-in characters. Each is drawn in code: no assets, works offline, no licence to track. */
export type CharacterModel = Exclude<Appearance['model'], 'vrm'>;

/** How a built-in character is drawn: flat (SVG), or modelled and lit (three.js). */
export type AvatarStyle = Appearance['style'];

export interface CharacterInfo {
  id: CharacterModel;
  name: string;
  blurb: string;
  presets: readonly { name: string; colours: LlamaColours }[];
  /** What each of the five colours paints on this character; a missing key isn't used by it. */
  colourLabels: Partial<Record<keyof LlamaColours, string>>;
  accessories: readonly Accessory[];
  /** Has a 3D version yet. */
  has3d: boolean;
}

/**
 * The built-in characters, as the studio offers them; the first preset is each one's default. In
 * characters.json so the phone apps' studio offers the same (client/scripts/protocol.mjs generates
 * their Kotlin copy).
 */
export const characters = catalogue as readonly CharacterInfo[];

/** The look the llama was drawn with; matches the server's default. */
export const classicColours: LlamaColours = characters[0]!.presets[0]!.colours;

export const defaultAppearance: Appearance = { model: 'llama', vrmId: null, colours: classicColours, accessory: 'none', style: '2d' };

/** The llama's starting points in the studio. Every colour can still be changed after picking one. */
export const presets = characters[0]!.presets;

export const characterInfo = (model: CharacterModel): CharacterInfo => characters.find((c) => c.id === model) ?? characters[0]!;

function channels(hex: string): [number, number, number] {
  const n = Number.parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function hex([r, g, b]: [number, number, number]): string {
  return `#${[r, g, b].map((c) => Math.round(Math.min(255, Math.max(0, c))).toString(16).padStart(2, '0')).join('')}`;
}

/** Blend towards another colour: 0 is `a`, 1 is `b`. */
export function mix(a: string, b: string, amount: number): string {
  const [ar, ag, ab] = channels(a);
  const [br, bg, bb] = channels(b);
  return hex([ar + (br - ar) * amount, ag + (bg - ag) * amount, ab + (bb - ab) * amount]);
}

// OKLCH: lightness, chroma, hue in a perceptual space. Blending hues here keeps colours vivid where
// an RGB blend greys out - the spot's violet tinted towards yellow came out a dusty pink.

const toLinear = (c: number) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
const fromLinear = (c: number) => (c <= 0.0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - 0.055);

export function toOklch(colour: string): [l: number, c: number, h: number] {
  const [r, g, b] = channels(colour).map((v) => toLinear(v / 255)) as [number, number, number];
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  const L = 0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s;
  const A = 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s;
  const B = 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s;
  return [L, Math.hypot(A, B), ((Math.atan2(B, A) * 180) / Math.PI + 360) % 360];
}

/** Back to #rrggbb, giving up chroma until the colour fits in sRGB. */
export function fromOklch(L: number, C: number, H: number): string {
  const rad = (H * Math.PI) / 180;
  let rgb: [number, number, number] = [0, 0, 0];
  for (let chroma = C, i = 0; i < 40; i++, chroma *= 0.95) {
    const a = chroma * Math.cos(rad);
    const b = chroma * Math.sin(rad);
    const l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3;
    const m = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3;
    const s = (L - 0.0894841775 * a - 1.291485548 * b) ** 3;
    rgb = [
      4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
      -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
      -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
    ];
    if (rgb.every((v) => v >= -0.0001 && v <= 1.0001)) break;
  }
  return hex(rgb.map((v) => fromLinear(Math.min(1, Math.max(0, v))) * 255) as [number, number, number]);
}

/** Blend in OKLCH, the short way round the hue circle. A near-grey takes the other colour's hue. */
export function mixVivid(a: string, b: string, amount: number): string {
  const [l1, c1, h1Raw] = toOklch(a);
  const [l2, c2, h2] = toOklch(b);
  const h1 = c1 < 0.03 ? h2 : h1Raw;
  const turn = ((h2 - h1 + 540) % 360) - 180;
  return fromOklch(l1 + (l2 - l1) * amount, c1 + (c2 - c1) * amount, (h1 + turn * amount + 360) % 360);
}

/** Lighter and more vivid, keeping the hue. */
export function brighten(colour: string, amount: number): string {
  const [l, c, h] = toOklch(colour);
  return fromOklch(Math.min(0.95, l + 0.12 * amount), c * (1 + 0.25 * amount), h);
}

/** 0 black, 1 white, by perceived brightness. */
export function lightness(colour: string): number {
  const [r, g, b] = channels(colour);
  return (0.299 * r + 0.587 * g + 0.114 * b) / 255;
}

/**
 * The llama's CSS custom properties for a set of colours. Shading is derived, so five choices
 * colour the whole toy consistently; the stitching darkens with the face so it always reads.
 */
export function llamaPalette(c: LlamaColours): Record<string, string> {
  const darkWool = lightness(c.wool) < 0.35;
  return {
    '--plush-wool': c.wool,
    // Dark fleece gets lighter outlines and curls; light fleece darker ones.
    '--plush-wool-shadow': darkWool ? mix(c.wool, '#ffffff', 0.22) : mix(c.wool, '#5b463d', 0.22),
    '--plush-skin': c.face,
    '--plush-skin-shadow': mix(c.face, '#3a2a22', 0.18),
    '--plush-snout': mix(c.face, '#3a2a22', 0.05),
    '--plush-ear': c.ear,
    '--plush-hoof': mix(c.ear, '#3a2a22', 0.25),
    '--plush-thread': mix(c.face, '#2a1d17', 0.72),
    '--plush-nose': mix(c.face, '#3a2a22', 0.55),
    '--plush-blush': c.blush,
    '--plush-accent': c.accent,
    '--plush-accent-shadow': mix(c.accent, '#000000', 0.3),
  };
}
