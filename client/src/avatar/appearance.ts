// How a familiar looks (PLAN.md §5.5). Mirrors AppearanceView in
// src/Familiar.Server/Familiars/Appearance.cs, which validates it: colours are #rrggbb and the
// accessory is one of the known ones before anything here puts them into CSS.

export type Accessory = 'none' | 'bow' | 'scarf' | 'glasses' | 'flower';

export const accessories: readonly Accessory[] = ['none', 'bow', 'scarf', 'glasses', 'flower'];

export interface LlamaColours {
  wool: string;
  face: string;
  ear: string;
  blush: string;
  accent: string;
}

/** The built-in characters. Each is drawn in code: no assets, works offline, no licence to track. */
export type CharacterModel = 'llama' | 'fox' | 'hedgehog' | 'monster' | 'spot';

export interface Appearance {
  model: CharacterModel | 'vrm';
  vrmId: string | null;
  colours: LlamaColours;
  accessory: Accessory;
}

/** The look the llama was drawn with; matches the server's default. */
export const classicColours: LlamaColours = { wool: '#fdfaf5', face: '#e4d6cc', ear: '#b3a093', blush: '#f4aab5', accent: '#7c5cff' };

export const defaultAppearance: Appearance = { model: 'llama', vrmId: null, colours: classicColours, accessory: 'none' };

/** The llama's starting points in the studio. Every colour can still be changed after picking one. */
export const presets: readonly { name: string; colours: LlamaColours }[] = [
  { name: 'Classic', colours: classicColours },
  { name: 'Cocoa', colours: { wool: '#8a6249', face: '#ead2bd', ear: '#5c3f2e', blush: '#f2a3a3', accent: '#e8b04a' } },
  { name: 'Storm', colours: { wool: '#b9bcc4', face: '#ece6e1', ear: '#6f7380', blush: '#f0a6b8', accent: '#3d7bd9' } },
  { name: 'Midnight', colours: { wool: '#3b3a48', face: '#d9cfc8', ear: '#22212b', blush: '#e891a6', accent: '#f5c542' } },
  { name: 'Rosy', colours: { wool: '#f7d4dc', face: '#f3e2d9', ear: '#d78ea0', blush: '#f08aa5', accent: '#8d5bd6' } },
  { name: 'Mint', colours: { wool: '#d3efe3', face: '#efe4da', ear: '#7fbfa5', blush: '#f4a9b4', accent: '#e86f5a' } },
];

export interface CharacterInfo {
  id: CharacterModel;
  name: string;
  blurb: string;
  presets: readonly { name: string; colours: LlamaColours }[];
  /** What each of the five colours paints on this character; a missing key isn't used by it. */
  colourLabels: Partial<Record<keyof LlamaColours, string>>;
  accessories: readonly Accessory[];
}

/** The built-in characters, as the studio offers them. The first preset is each one's default. */
export const characters: readonly CharacterInfo[] = [
  {
    id: 'llama',
    name: 'Plush llama',
    blurb: 'Big ears that show every feeling, and spectacular lashes.',
    presets,
    colourLabels: { wool: 'Fleece', face: 'Face', ear: 'Ears & hooves', blush: 'Cheeks', accent: 'Accessory' },
    accessories,
  },
  {
    id: 'fox',
    name: 'Fox',
    blurb: 'Swivelling pointed ears and a bushy tail that swishes when it’s happy.',
    presets: [
      { name: 'Red fox', colours: { wool: '#e0773a', face: '#fbf1e4', ear: '#3a2721', blush: '#f4a0a0', accent: '#2e7d8c' } },
      { name: 'Arctic', colours: { wool: '#eef1f5', face: '#ffffff', ear: '#7d8696', blush: '#f2a6b8', accent: '#3d7bd9' } },
      { name: 'Silver', colours: { wool: '#6f7280', face: '#e8e6e3', ear: '#2b2c33', blush: '#eb98ad', accent: '#e8b04a' } },
      { name: 'Fennec', colours: { wool: '#e9c99b', face: '#fbf3e6', ear: '#c98f6b', blush: '#f4a9a0', accent: '#d6577c' } },
    ],
    colourLabels: { wool: 'Coat', face: 'Muzzle & chest', ear: 'Inner ears & paws', blush: 'Cheeks', accent: 'Accessory' },
    accessories,
  },
  {
    id: 'hedgehog',
    name: 'Hedgehog',
    blurb: 'Soft prickles that bristle when it’s startled and lie flat when it’s sad.',
    presets: [
      { name: 'Hedgerow', colours: { wool: '#7a5a43', face: '#f0dcc3', ear: '#c9a688', blush: '#f2a3a3', accent: '#5d9a4a' } },
      { name: 'Snowy', colours: { wool: '#d8d2c8', face: '#fbf6ef', ear: '#e3c5c2', blush: '#f4a9b8', accent: '#8d5bd6' } },
      { name: 'Cinnamon', colours: { wool: '#b06a3b', face: '#f6e2cc', ear: '#d99c7a', blush: '#f19a8e', accent: '#2e7d8c' } },
    ],
    colourLabels: { wool: 'Prickles', face: 'Face', ear: 'Inner ears & paws', blush: 'Cheeks', accent: 'Accessory' },
    accessories,
  },
  {
    id: 'monster',
    name: 'Fuzzy monster',
    blurb: 'Googly eyes, stubby horns and fur that stands on end when it’s surprised.',
    presets: [
      { name: 'Teal', colours: { wool: '#3bb3a8', face: '#a8e6d8', ear: '#f4d35e', blush: '#f58f9e', accent: '#e8505b' } },
      { name: 'Grape', colours: { wool: '#7b52c7', face: '#c9b4ef', ear: '#ffd166', blush: '#ff8fab', accent: '#06d6a0' } },
      { name: 'Tangerine', colours: { wool: '#f08a36', face: '#ffd2a8', ear: '#6c4ab6', blush: '#ef6f8a', accent: '#118ab2' } },
      { name: 'Bubblegum', colours: { wool: '#f28bbd', face: '#ffd0e6', ear: '#8ecae6', blush: '#e5527f', accent: '#ffb703' } },
    ],
    colourLabels: { wool: 'Fur', face: 'Belly', ear: 'Horns & paws', blush: 'Cheeks', accent: 'Accessory' },
    accessories,
  },
  {
    id: 'spot',
    name: 'Spot',
    blurb: 'No face at all: it shows how it feels with its shape, its surface and its colour.',
    presets: [
      { name: 'Violet', colours: { wool: '#7c5cff', face: '#7c5cff', ear: '#7c5cff', blush: '#7c5cff', accent: '#c4b5ff' } },
      { name: 'Ocean', colours: { wool: '#2a9d8f', face: '#2a9d8f', ear: '#2a9d8f', blush: '#2a9d8f', accent: '#8ee3ef' } },
      { name: 'Ember', colours: { wool: '#e76f51', face: '#e76f51', ear: '#e76f51', blush: '#e76f51', accent: '#f4a261' } },
      { name: 'Moon', colours: { wool: '#d9d6cf', face: '#d9d6cf', ear: '#d9d6cf', blush: '#d9d6cf', accent: '#ffffff' } },
    ],
    colourLabels: { wool: 'Colour', accent: 'Glow' },
    accessories: ['none'],
  },
];

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
