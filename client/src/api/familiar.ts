import type { FamiliarView, VoicesView } from '../protocol/generated';
import { api } from './client';

/** Who the familiar is, how it sounds and how it looks (the server's FamiliarView). */
export type FamiliarProfile = FamiliarView;

export type Voices = VoicesView;

export const familiarApi = {
  get: () => api<FamiliarProfile>('/api/familiar'),
  save: (profile: FamiliarProfile) => api<FamiliarProfile>('/api/familiar', { method: 'PUT', body: profile }),
  voices: () => api<Voices>('/api/familiar/voices'),
  previewUrl: (voice: string, speed: number) =>
    `/api/familiar/voice-preview?voice=${encodeURIComponent(voice)}&speed=${speed}`,
};
