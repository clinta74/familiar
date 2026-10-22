import { describe, expect, it } from 'vitest';
import type { NewInviteView } from '../protocol/generated';
import { inviteLink } from './InviteAdmin';

const made = (link: string | null) => ({ code: 'finv_abc_def', link, invite: {} }) as unknown as NewInviteView;

describe('inviteLink', () => {
  it('uses the server’s link when it knows its public address', () => {
    expect(inviteLink(made('https://familiar.example.net/register?invite=finv_abc_def'), 'http://nas:7280'))
      .toBe('https://familiar.example.net/register?invite=finv_abc_def');
  });

  it('otherwise makes it from the address the page is on', () => {
    expect(inviteLink(made(null), 'https://familiar.example.net')).toBe('https://familiar.example.net/register?invite=finv_abc_def');
  });
});
