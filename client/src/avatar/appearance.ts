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

export interface Appearance {
  model: 'llama' | 'vrm';
  vrmId: string | null;
  colours: LlamaColours;
  accessory: Accessory;
}

/** The look the llama was drawn with; matches the server's default. */
export const classicColours: LlamaColours = { wool: '#fdfaf5', face: '#e4d6cc', ear: '#b3a093', blush: '#f4aab5', accent: '#7c5cff' };

export const defaultAppearance: Appearance = { model: 'llama', vrmId: null, colours: classicColours, accessory: 'none' };

/** Starting points in the studio. Every colour can still be changed after picking one. */
export const presets: readonly { name: string; colours: LlamaColours }[] = [
  { name: 'Classic', colours: classicColours },
  { name: 'Cocoa', colours: { wool: '#8a6249', face: '#ead2bd', ear: '#5c3f2e', blush: '#f2a3a3', accent: '#e8b04a' } },
  { name: 'Storm', colours: { wool: '#b9bcc4', face: '#ece6e1', ear: '#6f7380', blush: '#f0a6b8', accent: '#3d7bd9' } },
  { name: 'Midnight', colours: { wool: '#3b3a48', face: '#d9cfc8', ear: '#22212b', blush: '#e891a6', accent: '#f5c542' } },
  { name: 'Rosy', colours: { wool: '#f7d4dc', face: '#f3e2d9', ear: '#d78ea0', blush: '#f08aa5', accent: '#8d5bd6' } },
  { name: 'Mint', colours: { wool: '#d3efe3', face: '#efe4da', ear: '#7fbfa5', blush: '#f4a9b4', accent: '#e86f5a' } },
];

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
