import { PlushLlamaAvatar } from './PlushLlamaAvatar';
import type { AvatarDriver } from './types';

/**
 * Where a VRM model is looked for. Drop one here (client/public/avatars/familiar.vrm; the folder is
 * gitignored, because model licences vary) and it replaces the built-in plush llama. Uploading a model per
 * user is phase 6.
 */
export const DEFAULT_VRM_URL = '/avatars/familiar.vrm';

async function vrmAvailable(url: string): Promise<boolean> {
  try {
    const response = await fetch(url, { method: 'HEAD' });
    // nginx and Vite both answer a missing file with index.html (SPA fallback), so a 200 alone
    // proves nothing.
    return response.ok && !(response.headers.get('content-type') ?? '').includes('text/html');
  } catch {
    return false;
  }
}

/** The VRM model if one is present and loads; otherwise the built-in plush llama. */
export async function createAvatar(container: HTMLElement, vrmUrl = DEFAULT_VRM_URL): Promise<AvatarDriver> {
  if (await vrmAvailable(vrmUrl)) {
    try {
      const { VrmAvatar } = await import('./VrmAvatar');
      return await VrmAvatar.load(container, vrmUrl);
    } catch (error) {
      console.warn('Could not load the VRM avatar; using the built-in llama.', error);
    }
  }

  return new PlushLlamaAvatar(container);
}
