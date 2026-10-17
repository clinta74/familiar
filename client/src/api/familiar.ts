import type { FamiliarView, PlaceView, VoicesView } from '../protocol/generated';
import { api } from './client';

/** Who the familiar is, how it sounds and how it looks (the server's FamiliarView). */
export type FamiliarProfile = FamiliarView;

export type Voices = VoicesView;

export type Place = PlaceView;

export const familiarApi = {
  get: () => api<FamiliarProfile>('/api/familiar'),
  save: (profile: FamiliarProfile) => api<FamiliarProfile>('/api/familiar', { method: 'PUT', body: profile }),
  voices: () => api<Voices>('/api/familiar/voices'),
  previewUrl: (voice: string, speed: number) =>
    `/api/familiar/voice-preview?voice=${encodeURIComponent(voice)}&speed=${speed}`,
  /** The places called [name], to pick home from (BACKLOG item 32). */
  places: (name: string) => api<Place[]>(`/api/familiar/places?name=${encodeURIComponent(name)}`),
  /** The town at a position; the server keeps it to about a kilometre. */
  placeAt: (latitude: number, longitude: number) => api<Place>(`/api/familiar/places/at?latitude=${latitude}&longitude=${longitude}`),
};
