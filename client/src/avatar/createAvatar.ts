import type { Appearance } from './appearance';
import type { PlushCharacter } from './plush/engine';
import { PlushLlamaAvatar } from './PlushLlamaAvatar';
import type { AvatarDriver } from './types';

/** Where an uploaded VRM model is fetched from (PLAN.md §5.5). */
export const vrmUrl = (id: string) => `/api/avatars/${encodeURIComponent(id)}/model`;

/** The built-in characters other than the llama load on demand, so the default costs nothing extra. */
async function character(container: HTMLElement, appearance: Appearance): Promise<PlushCharacter> {
  switch (appearance.model) {
    case 'fox':
      return new (await import('./PlushFoxAvatar')).PlushFoxAvatar(container, appearance);
    case 'hedgehog':
      return new (await import('./PlushHedgehogAvatar')).PlushHedgehogAvatar(container, appearance);
    case 'monster':
      return new (await import('./PlushMonsterAvatar')).PlushMonsterAvatar(container, appearance);
    case 'spot':
      return new (await import('./SpotAvatar')).SpotAvatar(container, appearance);
    default:
      return new PlushLlamaAvatar(container, appearance);
  }
}

/**
 * The familiar as it should look: the user's uploaded VRM model if they chose one and it loads,
 * otherwise the built-in character they picked, in their colours.
 */
export async function createAvatar(container: HTMLElement, appearance: Appearance): Promise<AvatarDriver> {
  if (appearance.model === 'vrm' && appearance.vrmId) {
    try {
      const { VrmAvatar } = await import('./VrmAvatar');
      return await VrmAvatar.load(container, vrmUrl(appearance.vrmId));
    } catch (error) {
      console.warn('Could not load the VRM model; using the plush llama.', error);
      return new PlushLlamaAvatar(container, appearance);
    }
  }

  if (appearance.style === '3d' && appearance.model === 'llama') {
    try {
      const { PlushLlama3D } = await import('./PlushLlama3D');
      return new PlushLlama3D(container, appearance);
    } catch (error) {
      // No WebGL (an old phone, a locked-down browser): the flat llama is still the llama.
      console.warn('Could not start the 3D llama; drawing it flat.', error);
    }
  }

  return character(container, appearance);
}
