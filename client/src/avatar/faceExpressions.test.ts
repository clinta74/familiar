import { describe, expect, it } from 'vitest';
import { approach, emotionFaces, faceFor, neutralFace } from './faceExpressions';
import { emotions } from './types';

describe('faceFor', () => {
  it('is neutral at zero intensity and the full face at one', () => {
    expect(faceFor('joy', 0)).toEqual(neutralFace);
    expect(faceFor('joy', 1)).toEqual(emotionFaces.joy);
  });

  it('clamps intensity', () => {
    expect(faceFor('sad', 5)).toEqual(emotionFaces.sad);
  });

  it('gives every emotion a face that differs from neutral, except neutral', () => {
    for (const emotion of emotions.filter((e) => e !== 'neutral')) {
      expect(faceFor(emotion, 1), emotion).not.toEqual(neutralFace);
    }
  });

  it('reads the right way round: joy smiles, sadness frowns, surprise opens wide', () => {
    expect(emotionFaces.joy.mouthCurve).toBeGreaterThan(neutralFace.mouthCurve);
    expect(emotionFaces.sad.mouthCurve).toBeLessThan(0);
    expect(emotionFaces.angry.browTilt).toBeGreaterThan(0);
    expect(emotionFaces.sad.browTilt).toBeLessThan(0);
    expect(emotionFaces.surprised.eyeOpen).toBeGreaterThan(1);
  });

  it('perks the ears for joy and surprise and droops them for sadness', () => {
    expect(emotionFaces.joy.earLift).toBeGreaterThan(0);
    expect(emotionFaces.surprised.earLift).toBe(1);
    expect(emotionFaces.sad.earLift).toBe(-1);
  });
});

describe('approach', () => {
  it('moves towards the target without overshooting', () => {
    const current = { ...neutralFace };
    approach(current, emotionFaces.surprised, 0.05, 9);
    expect(current.mouthOpen).toBeGreaterThan(0);
    expect(current.mouthOpen).toBeLessThan(emotionFaces.surprised.mouthOpen);

    for (let i = 0; i < 200; i++) approach(current, emotionFaces.surprised, 0.05, 9);
    expect(current.mouthOpen).toBeCloseTo(emotionFaces.surprised.mouthOpen, 5);
  });
});
