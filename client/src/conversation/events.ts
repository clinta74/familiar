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
  speechAvailable: boolean;
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

/** A spoken sentence is ready to fetch and play, in `seq` order. */
export interface AudioEvent {
  replyId: string;
  seq: number;
  url: string;
  text: string;
  words?: { word: string; start: number; end: number }[] | null;
}

/** The reply was cut to what the user heard before talking over it. */
export interface ReplyTruncatedEvent {
  id: string;
  text: string;
}

export type JobKind = 'timer' | 'reminder';

/** A pending timer or reminder. */
export interface JobView {
  id: string;
  kind: JobKind;
  label: string;
  dueAt: string;
  durationSeconds: number | null;
  createdAt: string;
}

/** The account's pending jobs, whenever they change. */
export interface JobsEvent {
  jobs: JobView[];
}

/** A timer or reminder went off; the reply announcing it arrives as usual. */
export interface NotifyEvent {
  /** The job, or the announcement. */
  id: string;
  kind: JobKind | 'announcement';
  /** What it was for, or who sent it. */
  label: string;
  text: string;
}

export interface ToolStartedEvent {
  replyId: string;
  callId: string;
  tool: string;
}

export interface ToolFinishedEvent {
  replyId: string;
  callId: string;
  tool: string;
  ok: boolean;
  summary: string | null;
}

/** The familiar wants to do something outside Familiar and is asking first. */
export interface ToolConfirmEvent {
  replyId: string;
  callId: string;
  tool: string;
  description: string;
  expiresAt: string;
}

/** A confirmation was answered (on any device) or expired. */
export interface ToolConfirmedEvent {
  callId: string;
  approved: boolean;
}
