import { describe, expect, it } from 'vitest';
import template from '../nginx.conf.template?raw';

// The deployed site's nginx headers. The dev server sends none, so a Permissions-Policy that refuses
// something the page uses only shows on the NAS: geolocation=() blocked "where am I" and picking home
// there whatever the user allowed (BACKLOG items 24, 32).
const policy = /Permissions-Policy "([^"]+)"/.exec(template)?.[1] ?? '';

describe('the deployed Permissions-Policy', () => {
  it('lets the site itself use the microphone and location', () => {
    expect(policy).toContain('microphone=(self)');
    expect(policy).toContain('geolocation=(self)');
  });
});
