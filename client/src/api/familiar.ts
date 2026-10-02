import { api } from './client';

export interface FamiliarProfile {
  name: string;
  userNickname: string | null;
  warmth: number;
  playfulness: number;
  verbosity: number;
  formality: number;
  expressiveness: number;
}

export const familiarApi = {
  get: () => api<FamiliarProfile>('/api/familiar'),
  save: (profile: FamiliarProfile) => api<FamiliarProfile>('/api/familiar', { method: 'PUT', body: profile }),
};
