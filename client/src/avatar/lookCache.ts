import type { Appearance } from './appearance';

/**
 * The familiar's look as last seen on this browser, per account: a returning visit builds the right
 * character straight away while the profile is re-checked, instead of waiting on it. Only a
 * convenience - storage can be unavailable (a private window), and then it's simply not used.
 */
const key = (username: string) => `familiar.look.${username}`;

export function cachedLook(username: string | undefined): Appearance | null {
  if (!username) return null;
  try {
    const raw = localStorage.getItem(key(username));
    return raw ? (JSON.parse(raw) as Appearance) : null;
  } catch {
    return null;
  }
}

export function rememberLook(username: string | undefined, look: Appearance): void {
  if (!username) return;
  try {
    localStorage.setItem(key(username), JSON.stringify(look));
  } catch {
    // Not kept; it's only a head start.
  }
}
