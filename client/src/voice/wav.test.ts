import { describe, expect, it } from 'vitest';
import { encodeWav } from './wav';

describe('encodeWav', () => {
  it('writes a 16 kHz mono PCM header and clamped samples', async () => {
    const blob = encodeWav(new Float32Array([0, 1, -1, 2]), 16000);
    const view = new DataView(await blob.arrayBuffer());
    const tag = (at: number) => String.fromCharCode(...[0, 1, 2, 3].map((i) => view.getUint8(at + i)));

    expect(blob.type).toBe('audio/wav');
    expect(blob.size).toBe(44 + 4 * 2);
    expect([tag(0), tag(8), tag(36)]).toEqual(['RIFF', 'WAVE', 'data']);
    expect(view.getUint32(24, true)).toBe(16000);
    expect([view.getInt16(44, true), view.getInt16(46, true), view.getInt16(48, true), view.getInt16(50, true)])
      .toEqual([0, 32767, -32768, 32767]);
  });
});
