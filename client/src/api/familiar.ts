import { api } from './client';

export interface FamiliarProfile {
  name: string;
  userNickname: string | null;
  warmth: number;
  playfulness: number;
  verbosity: number;
  formality: number;
  expressiveness: number;
  voice: string | null;
  voiceSpeed: number;
  /** May speak first: a hello when you come back, mentioning what's coming up. */
  proactive: boolean;
}

export interface Voices {
  voices: string[];
  default: string;
}

export const familiarApi = {
  get: () => api<FamiliarProfile>('/api/familiar'),
  save: (profile: FamiliarProfile) => api<FamiliarProfile>('/api/familiar', { method: 'PUT', body: profile }),
  voices: () => api<Voices>('/api/familiar/voices'),
  previewUrl: (voice: string, speed: number) =>
    `/api/familiar/voice-preview?voice=${encodeURIComponent(voice)}&speed=${speed}`,
};
