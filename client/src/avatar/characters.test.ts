import { describe, expect, it } from 'vitest';
import { characters, lightness } from './appearance';
import { spinePath } from './PlushHedgehogAvatar';
import { blendShape, spotColour, spotPath } from './SpotAvatar';
import { emotions, type Emotion } from './types';

const only = (emotion: Emotion, amount = 1): Record<Emotion, number> =>
  Object.fromEntries(emotions.map((e) => [e, e === emotion ? amount : e === 'neutral' ? 1 - amount : 0])) as Record<Emotion, number>;

/** The points a path passes through (every C segment's end point). */
const points = (d: string) => [...d.matchAll(/ (-?[\d.]+),(-?[\d.]+)(?= C| Z)/g)].map((m) => [Number(m[1]), Number(m[2])] as const);

describe('the spot', () => {
  it('draws the same closed outline for the same shape and time', () => {
    const shape = blendShape(only('neutral'));
    expect(spotPath(shape, 1.5)).toBe(spotPath(shape, 1.5));
    expect(spotPath(shape, 1.5)).toMatch(/^M.* Z$/);
  });

  it('sags when sad: wider, lower and flatter than when content', () => {
    const extent = (mood: Record<Emotion, number>) => {
      const ps = points(spotPath(blendShape(mood), 0));
      const xs = ps.map((p) => p[0]);
      const ys = ps.map((p) => p[1]);
      return { width: Math.max(...xs) - Math.min(...xs), height: Math.max(...ys) - Math.min(...ys), bottom: Math.max(...ys) };
    };
    const content = extent(only('neutral'));
    const sad = extent(only('sad'));
    expect(sad.width / sad.height).toBeGreaterThan(content.width / content.height * 1.2);
    expect(sad.height).toBeLessThan(content.height);
  });

  it('swells when surprised and goes spiky when cross', () => {
    expect(blendShape(only('surprised')).size).toBeGreaterThan(blendShape(only('neutral')).size);
    expect(blendShape(only('angry')).spikes).toBeGreaterThan(0.1);
    expect(blendShape(only('angry')).roughness).toBeGreaterThan(blendShape(only('relaxed')).roughness);
  });

  it('blends shapes by how much of each emotion is showing', () => {
    const half = blendShape(only('sad', 0.5));
    expect(half.sag).toBeGreaterThan(0);
    expect(half.sag).toBeLessThan(blendShape(only('sad')).sag);
  });

  it('takes on the colour of its mood, and keeps its own when calm', () => {
    const base = '#808080';
    expect(spotColour(base, only('neutral'))).toBe(base);
    const [, , blueSad] = spotColour(base, only('sad')).match(/#(..)(..)(..)/)!.slice(1).map((h) => Number.parseInt(h, 16));
    const [redSad] = spotColour(base, only('sad')).match(/#(..)(..)(..)/)!.slice(1).map((h) => Number.parseInt(h, 16));
    expect(blueSad!).toBeGreaterThan(redSad!);
    expect(lightness(spotColour(base, only('joy')))).toBeGreaterThan(lightness(base));
  });
});

describe('the hedgehog', () => {
  it('has one spike per prickle, and longer prickles when bristling', () => {
    const flat = spinePath(0, 0, 58, Math.PI, Math.PI * 2, 10, 10, 0, 0);
    const bristling = spinePath(0, 0, 58, Math.PI, Math.PI * 2, 10, 34, 0, 0);
    expect(flat.match(/M/g)).toHaveLength(10);
    const reach = (d: string) => Math.max(...[...d.matchAll(/(-?[\d.]+),(-?[\d.]+)/g)].map((m) => Math.hypot(Number(m[1]), Number(m[2]))));
    expect(reach(bristling)).toBeGreaterThan(reach(flat) + 15);
  });
});

describe('the characters', () => {
  it('each have presets with every colour as #rrggbb, and labels for what they use', () => {
    for (const c of characters) {
      expect(c.presets.length).toBeGreaterThan(0);
      for (const p of c.presets) for (const v of Object.values(p.colours)) expect(v).toMatch(/^#[0-9a-f]{6}$/);
      expect(c.colourLabels.wool).toBeTruthy();
    }
    expect(characters.map((c) => c.id)).toEqual(['llama', 'fox', 'hedgehog', 'monster', 'spot']);
  });
});
