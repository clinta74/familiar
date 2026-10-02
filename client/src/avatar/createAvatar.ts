import type { Appearance } from './appearance';
import { PlushLlamaAvatar } from './PlushLlamaAvatar';
import type { AvatarDriver } from './types';

/** Where an uploaded VRM model is fetched from (PLAN.md §5.5). */
export const vrmUrl = (id: string) => `/api/avatars/${encodeURIComponent(id)}/model`;

/**
 * The familiar as it should look: the user's uploaded VRM model if they chose one and it loads,
 * otherwise the plush llama in their colours.
 */
export async function createAvatar(container: HTMLElement, appearance: Appearance): Promise<AvatarDriver> {
  if (appearance.model === 'vrm' && appearance.vrmId) {
    try {
      const { VrmAvatar } = await import('./VrmAvatar');
      return await VrmAvatar.load(container, vrmUrl(appearance.vrmId));
    } catch (error) {
      console.warn('Could not load the VRM model; using the plush llama.', error);
    }
  }

  return new PlushLlamaAvatar(container, appearance);
}
