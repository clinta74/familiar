import { describe, expect, it } from 'vitest';
import page from '../../avatar.html?raw';

// The page the phone apps bundle sets its own Content Security Policy.
const policy = page.match(/http-equiv="Content-Security-Policy" content="([^"]+)"/)?.[1] ?? '';
const directive = (name: string) =>
  policy.split(';').map((d) => d.trim().split(/\s+/)).find(([n]) => n === name)?.slice(1) ?? [];

describe('the avatar page the phone apps bundle', () => {
  it('lets the 3D models decompress', () => {
    // The GLBs are meshopt-compressed, decoded by WebAssembly. Blocked, every 3D character quietly
    // fell back to the flat drawing in the app (found on the Fold, 2026-10-03).
    expect(directive('script-src')).toContain("'wasm-unsafe-eval'");
  });

  it('still runs no script but its own', () => {
    expect(directive('script-src')).not.toContain("'unsafe-eval'");
    expect(directive('script-src')).not.toContain("'unsafe-inline'");
    expect(directive('default-src')).toEqual(["'self'"]);
  });
});
