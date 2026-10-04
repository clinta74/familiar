import { describe, expect, it } from 'vitest';
import { accessories, characters, fromOklch, lightness, mixVivid, toOklch, type CharacterModel } from './appearance';
import { tuftPath } from './PlushHedgehogAvatar';
import { blendShape, spotColour, spotCore, spotPath } from './SpotAvatar';
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

describe('the happy colour', () => {
  const violet = '#7c5cff';

  it('keeps a violet spot violet - brighter, not the dusty pink an RGB blend with yellow gave', () => {
    const happy = spotColour(violet, only('joy'));
    const [l0, , h0] = toOklch(violet);
    const [l1, , h1] = toOklch(happy);
    expect(Math.abs(h1 - h0)).toBeLessThan(5);
    expect(l1).toBeGreaterThan(l0 + 0.08);
  });

  it('lights it warm from inside', () => {
    const neutral = toOklch(spotCore(violet, 0, 0.5));
    const happy = toOklch(spotCore(spotColour(violet, only('joy')), 1, 0.5));
    // Warm: the core's hue moves from violet (~286) round towards yellow (~95).
    expect(happy[2]).toBeLessThan(neutral[2] - 100);
    expect(happy[0]).toBeGreaterThan(neutral[0]);
  });

  it('round-trips through OKLCH, and blends complementary colours without going grey', () => {
    for (const c of ['#7c5cff', '#2a9d8f', '#e76f51', '#123456']) expect(fromOklch(...toOklch(c))).toBe(c);
    const [, rgbChroma] = toOklch('#c49ba2');
    const [, vividChroma] = toOklch(mixVivid(violet, '#ffcf56', 0.55));
    expect(vividChroma).toBeGreaterThan(rgbChroma * 2);
  });
});

describe('the hedgehog', () => {
  it('has one tuft per prickle, and longer prickles when bristling', () => {
    const flat = tuftPath(58, 58, 0, 0, 10, 6, 0, 0);
    const bristling = tuftPath(58, 58, 0, 0, 10, 26, 0, 0);
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
    expect(characters.map((c) => c.id)).toEqual(['llama', 'fox', 'hedgehog', 'monster', 'otter', 'giraffe', 'spot']);
  });

  it('are exactly the models the server accepts, wearing only what it accepts (characters.json)', () => {
    // A Record over the generated type: adding a model to the server's AppearanceView without
    // listing it here fails to compile, and the catalogue must match it.
    const models: Record<CharacterModel, true> = { llama: true, fox: true, hedgehog: true, monster: true, otter: true, giraffe: true, spot: true };
    expect(characters.map((c) => c.id).sort()).toEqual(Object.keys(models).sort());
    for (const c of characters) {
      expect(c.accessories[0]).toBe('none');
      for (const a of c.accessories) expect(accessories).toContain(a);
      for (const key of Object.keys(c.colourLabels)) expect(['wool', 'face', 'ear', 'blush', 'accent']).toContain(key);
    }
  });
});
