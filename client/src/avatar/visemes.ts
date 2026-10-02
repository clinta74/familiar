/**
 * Mouth shapes for speech (PLAN.md §5.4). The five vowel shapes are VRM's own presets (aa ih ou ee
 * oh); `closed` is m/b/p, where the lips meet; `teeth` is f/v.
 */
export type Viseme = 'aa' | 'ih' | 'ou' | 'ee' | 'oh' | 'closed' | 'teeth';

/** A word as Kokoro timed it, in seconds from the start of its clip. */
export interface TimedWord {
  word: string;
  start: number;
  end: number;
}

const letterShapes: Record<string, Viseme> = {
  a: 'aa', e: 'ee', i: 'ih', o: 'oh', u: 'ou', y: 'ee', w: 'ou', r: 'ou',
  m: 'closed', b: 'closed', p: 'closed',
  f: 'teeth', v: 'teeth',
};

/**
 * The shape of the mouth `fraction` of the way through `word`. English spelling isn't phonetics,
 * but at speaking speed a letter-level approximation reads as the right word far better than a mouth
 * that only opens and closes.
 */
export function visemeFor(word: string, fraction: number): Viseme | null {
  const letters = word.toLowerCase().replace(/[^a-z]/g, '');
  if (letters.length === 0) return null; // punctuation: a pause

  const index = Math.min(letters.length - 1, Math.max(0, Math.floor(fraction * letters.length)));
  const at = letters[index]!;

  // Vowel pairs that sound like neither letter alone: moon, see, sea, out, now.
  const pair = (letters[index - 1] ?? '') + at + (letters[index + 1] ?? '');
  if (/oo/.test(pair) || /o[uw]/.test(pair)) return 'ou';
  if (/e[ea]/.test(pair)) return 'ee';

  if (letterShapes[at]) return letterShapes[at];

  // Other consonants borrow the shape of the nearest vowel, as mouths do between sounds.
  for (let d = 1; d < letters.length; d++) {
    for (const j of [index + d, index - d]) {
      const shape = letters[j] && letterShapes[letters[j]!];
      if (shape && shape !== 'closed' && shape !== 'teeth') return shape;
    }
  }
  return 'ih';
}

/** The viseme at `time` seconds into a clip with these word timings. */
export function visemeAt(words: readonly TimedWord[], time: number): Viseme | null {
  const word = words.find((w) => time >= w.start && time < w.end);
  if (!word) return null;
  const span = Math.max(0.001, word.end - word.start);
  return visemeFor(word.word, (time - word.start) / span);
}

/** How each shape sets the plush llama's mouth: openness, width (1 = rest), corner lift. */
export const visemeMouth: Record<Viseme, { open: number; width: number; curve: number }> = {
  aa: { open: 0.85, width: 1.05, curve: 0.2 },
  ih: { open: 0.45, width: 1.15, curve: 0.3 },
  ee: { open: 0.3, width: 1.35, curve: 0.45 },
  oh: { open: 0.7, width: 0.7, curve: 0 },
  ou: { open: 0.45, width: 0.5, curve: 0 },
  closed: { open: 0, width: 0.9, curve: 0.1 },
  teeth: { open: 0.15, width: 1.05, curve: 0.1 },
};
