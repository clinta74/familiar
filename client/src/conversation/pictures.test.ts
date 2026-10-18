import { describe, expect, it } from 'vitest';
import { fitted, isPicture, pictureUrl } from './pictures';

describe('pictures (BACKLOG item 35)', () => {
  it('shrinks the longer side to 1024, keeping the shape, and never enlarges', () => {
    expect(fitted(4032, 3024)).toEqual({ width: 1024, height: 768 });
    expect(fitted(3024, 4032)).toEqual({ width: 768, height: 1024 });
    expect(fitted(800, 600)).toEqual({ width: 800, height: 600 });
    expect(fitted(5000, 2)).toEqual({ width: 1024, height: 1 });
  });

  it('takes pictures only, and shows them from the server', () => {
    expect(isPicture(new Blob([], { type: 'image/heic' }))).toBe(true);
    expect(isPicture(new Blob([], { type: 'application/pdf' }))).toBe(false);
    expect(pictureUrl('abc')).toBe('/api/conversation/images/abc');
  });
});
