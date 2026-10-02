import type { Emotion } from '../avatar/types';
import type { FamiliarState, MessageView, ReadyEvent, ReplyEndEvent } from './events';

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  emotion?: Emotion;
  interrupted?: boolean;
  /** The reply is still arriving. */
  streaming?: boolean;
}

export type Connection = 'connecting' | 'open' | 'reconnecting';

export interface ConversationState {
  connection: Connection;
  familiarName: string;
  modelAvailable: boolean;
  /** Speech-to-text and text-to-speech are both reachable. */
  speechAvailable: boolean;
  state: FamiliarState;
  messages: ChatMessage[];
  error: string | null;
}

export const initialConversation: ConversationState = {
  connection: 'connecting',
  familiarName: 'your familiar',
  modelAvailable: true,
  speechAvailable: false,
  state: 'idle',
  messages: [],
  error: null,
};

export type ConversationAction =
  | { type: 'connection'; connection: Connection }
  | { type: 'ready'; ready: ReadyEvent }
  | { type: 'history'; messages: MessageView[] }
  | { type: 'state'; state: FamiliarState }
  | { type: 'message'; message: MessageView }
  | { type: 'replyStart'; id: string }
  | { type: 'replyDelta'; id: string; text: string }
  | { type: 'replyEnd'; end: ReplyEndEvent }
  | { type: 'replyTruncated'; id: string; text: string }
  | { type: 'cleared' }
  | { type: 'error'; message: string | null };

function fromView(view: MessageView): ChatMessage {
  return {
    id: view.id,
    role: view.role,
    text: view.text,
    emotion: view.emotion ?? undefined,
    interrupted: view.interrupted || undefined,
  };
}

/**
 * Every event is idempotent by message id: a reconnect replays events the client may already have
 * applied, and history loaded on startup can overlap the live stream.
 */
export function conversationReducer(state: ConversationState, action: ConversationAction): ConversationState {
  switch (action.type) {
    case 'connection':
      return { ...state, connection: action.connection };

    case 'ready':
      return {
        ...state,
        connection: 'open',
        familiarName: action.ready.familiar.name,
        modelAvailable: action.ready.modelAvailable,
        speechAvailable: action.ready.speechAvailable,
        state: action.ready.modelAvailable ? state.state : 'resting',
      };

    case 'history': {
      // Keep anything the stream delivered that the history snapshot didn't include yet.
      const known = new Set(action.messages.map((m) => m.id));
      const live = state.messages.filter((m) => !known.has(m.id));
      return { ...state, messages: [...action.messages.map(fromView), ...live] };
    }

    case 'state':
      return {
        ...state,
        state: action.state,
        modelAvailable: action.state === 'resting' ? false : action.state === 'speaking' ? true : state.modelAvailable,
        error: action.state === 'thinking' ? null : state.error,
      };

    case 'message':
      if (state.messages.some((m) => m.id === action.message.id)) return state;
      return { ...state, messages: [...state.messages, fromView(action.message)] };

    case 'replyStart':
      if (state.messages.some((m) => m.id === action.id)) return state;
      return { ...state, messages: [...state.messages, { id: action.id, role: 'assistant', text: '', streaming: true }] };

    case 'replyDelta':
      return {
        ...state,
        messages: state.messages.map((m) =>
          m.id === action.id && m.streaming ? { ...m, text: m.text + action.text } : m),
      };

    case 'replyEnd': {
      const end = action.end;
      const done: ChatMessage = {
        id: end.id,
        role: 'assistant',
        text: end.text,
        emotion: end.emotion,
        interrupted: end.interrupted || undefined,
      };
      const exists = state.messages.some((m) => m.id === end.id);
      return {
        ...state,
        messages: exists ? state.messages.map((m) => (m.id === end.id ? done : m)) : [...state.messages, done],
      };
    }

    case 'replyTruncated':
      return {
        ...state,
        messages: state.messages.map((m) =>
          m.id === action.id ? { ...m, text: action.text, interrupted: true, streaming: false } : m),
      };

    case 'cleared':
      return { ...state, messages: [], error: null };

    case 'error':
      return { ...state, error: action.message };
  }
}
