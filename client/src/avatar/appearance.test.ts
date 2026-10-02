import { describe, expect, it } from 'vitest';
import { classicColours, lightness, llamaPalette, mix, presets } from './appearance';

describe('llama colours', () => {
  it('reproduce the hand-picked classic llama exactly where they are given', () => {
    const palette = llamaPalette(classicColours);
    expect(palette['--plush-wool']).toBe('#fdfaf5');
    expect(palette['--plush-skin']).toBe('#e4d6cc');
    expect(palette['--plush-ear']).toBe('#b3a093');
  });

  it('outline dark fleece in a lighter shade and light fleece in a darker one', () => {
    const midnight = presets.find((p) => p.name === 'Midnight')!.colours;
    expect(lightness(llamaPalette(midnight)['--plush-wool-shadow']!)).toBeGreaterThan(lightness(midnight.wool));
    expect(lightness(llamaPalette(classicColours)['--plush-wool-shadow']!)).toBeLessThan(lightness(classicColours.wool));
  });

  it('only ever produce #rrggbb', () => {
    for (const { colours } of presets) {
      for (const value of Object.values(llamaPalette(colours))) expect(value).toMatch(/^#[0-9a-f]{6}$/);
    }
  });

  it('mix between two colours', () => {
    expect(mix('#000000', '#ffffff', 0.5)).toBe('#808080');
    expect(mix('#102030', '#102030', 0.7)).toBe('#102030');
  });
});
