import { describe, expect, it } from 'vitest';
import { visemeAt, visemeFor } from './visemes';

describe('visemes', () => {
  it('maps vowels and lip consonants to shapes', () => {
    expect(visemeFor('ah', 0)).toBe('aa');
    expect(visemeFor('see', 0.9)).toBe('ee');
    expect(visemeFor('go', 0.9)).toBe('oh');
    expect(visemeFor('mum', 0)).toBe('closed');
    expect(visemeFor('five', 0)).toBe('teeth');
  });

  it('gives other consonants the nearest vowel shape', () => {
    expect(visemeFor('stop', 0)).toBe('oh');
  });

  it('treats punctuation as a pause', () => {
    expect(visemeFor(',', 0.5)).toBeNull();
  });

  it('finds the word being spoken at a moment in the clip', () => {
    const words = [
      { word: 'Hello', start: 0.0, end: 0.3 },
      { word: 'moon', start: 0.3, end: 0.7 },
    ];
    expect(visemeAt(words, 0.31)).toBe('closed');
    expect(visemeAt(words, 0.5)).toBe('ou');
    expect(visemeAt(words, 0.9)).toBeNull();
  });
});
