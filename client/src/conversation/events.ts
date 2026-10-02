import type { Emotion, Gesture } from '../avatar/types';

// Payloads of the conversation stream's events (PLAN.md §3.1). Mirrors
// src/Familiar.Server/Conversation/ConversationEvents.cs; generated from a schema once the phone
// apps need the same types (MOBILE.md §6).

export type FamiliarState = 'idle' | 'thinking' | 'speaking' | 'resting';

export interface MessageView {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  emotion: Emotion | null;
  intensity: number | null;
  interrupted: boolean;
  createdAt: string;
}

export interface ReadyEvent {
  familiar: { name: string; expressiveness: number };
  modelAvailable: boolean;
}

export interface StateEvent {
  state: FamiliarState;
}

export interface ReplyStartEvent {
  id: string;
}

export interface ReplyDeltaEvent {
  id: string;
  text: string;
}

export interface ReplyEndEvent {
  id: string;
  text: string;
  emotion: Emotion;
  intensity: number;
  interrupted: boolean;
}

export interface ExpressionEvent {
  emotion: Emotion;
  intensity: number;
}

export interface GestureEvent {
  gesture: Gesture;
}

export interface ErrorEvent {
  code: string;
  message: string;
}
