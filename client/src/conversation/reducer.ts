import type { Emotion } from '../avatar/types';
import type {
  FamiliarState,
  JobView,
  LinkView,
  MessageView,
  ReadyEvent,
  ReplyEndEvent,
  ToolConfirmEvent,
  ToolFinishedEvent,
  ToolStartedEvent,
} from './events';

/** A tool the familiar used during a reply, shown as a chip on it. */
export interface ToolChip {
  callId: string;
  tool: string;
  /** Still running. */
  running: boolean;
  ok?: boolean;
  summary?: string | null;
}

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  emotion?: Emotion;
  interrupted?: boolean;
  /** The reply is still arriving. */
  streaming?: boolean;
  /** Tools it used, in order. */
  tools?: ToolChip[];
  /** Pages it handed over, as cards under the reply (BACKLOG item 34). */
  links?: LinkView[];
  /** A picture sent with the message (BACKLOG item 35). */
  imageId?: string;
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
  /** Pending timers and reminders, soonest first. */
  jobs: JobView[];
  /** A tool waiting for the user's say-so. */
  confirm: ToolConfirmEvent | null;
}

export const initialConversation: ConversationState = {
  connection: 'connecting',
  familiarName: 'your familiar',
  modelAvailable: true,
  speechAvailable: false,
  state: 'idle',
  messages: [],
  error: null,
  jobs: [],
  confirm: null,
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
  | { type: 'jobs'; jobs: JobView[] }
  | { type: 'toolStarted'; event: ToolStartedEvent }
  | { type: 'toolFinished'; event: ToolFinishedEvent }
  | { type: 'confirm'; event: ToolConfirmEvent }
  | { type: 'confirmed'; callId: string }
  | { type: 'error'; message: string | null };

function fromView(view: MessageView): ChatMessage {
  return {
    id: view.id,
    role: view.role,
    text: view.text,
    emotion: view.emotion ?? undefined,
    interrupted: view.interrupted || undefined,
    links: view.links?.length ? view.links : undefined,
    imageId: view.imageId ?? undefined,
  };
}

/**
 * What the stream delivered that the history snapshot doesn't have yet, to keep after it. Not
 * everything it lacks: reloaded after a while away (a resync), the newest messages have moved the
 * window on, and what dropped out of it is older - kept, it landed at the bottom out of order.
 * Message ids are time-ordered (UUID v7), so they say which is which.
 */
function newerThan(history: MessageView[], messages: ChatMessage[]): ChatMessage[] {
  const known = new Set(history.map((m) => m.id));
  const oldest = history[0]?.id;
  return messages.filter((m) => !known.has(m.id) && (oldest === undefined ? m.streaming : m.id > oldest));
}

/** The reply's bubble, created if the tool ran before it said anything (the usual order). */
function withChip(state: ConversationState, replyId: string, update: (chips: ToolChip[]) => ToolChip[]): ConversationState {
  const exists = state.messages.some((m) => m.id === replyId);
  const messages = exists
    ? state.messages
    : [...state.messages, { id: replyId, role: 'assistant' as const, text: '', streaming: true }];
  return {
    ...state,
    messages: messages.map((m) => (m.id === replyId ? { ...m, tools: update(m.tools ?? []) } : m)),
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

    case 'history':
      return { ...state, messages: [...action.messages.map(fromView), ...newerThan(action.messages, state.messages)] };

    case 'state':
      return {
        ...state,
        // Idle means nothing more is coming: a reply that only ran a tool and said nothing is done.
        messages: action.state === 'idle' && state.messages.some((m) => m.streaming)
          ? state.messages.map((m) => (m.streaming ? { ...m, streaming: false } : m))
          : state.messages,
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
      const existing = state.messages.find((m) => m.id === end.id);
      const done: ChatMessage = {
        id: end.id,
        role: 'assistant',
        text: end.text,
        emotion: end.emotion,
        interrupted: end.interrupted || undefined,
        tools: existing?.tools,
        links: end.links?.length ? end.links : undefined,
      };
      const exists = existing !== undefined;
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
      return { ...state, messages: [], error: null, confirm: null };

    case 'jobs':
      return { ...state, jobs: action.jobs };

    case 'toolStarted': {
      const { replyId, callId, tool } = action.event;
      return withChip(state, replyId, (chips) =>
        chips.some((c) => c.callId === callId) ? chips : [...chips, { callId, tool, running: true }]);
    }

    case 'toolFinished': {
      const { replyId, callId, tool, ok, summary } = action.event;
      const finished: ToolChip = { callId, tool, running: false, ok, summary };
      const next = withChip(state, replyId, (chips) =>
        chips.some((c) => c.callId === callId)
          ? chips.map((c) => (c.callId === callId ? finished : c))
          : [...chips, finished]);
      return state.confirm?.callId === callId ? { ...next, confirm: null } : next;
    }

    case 'confirm':
      return { ...state, confirm: action.event };

    case 'confirmed':
      return state.confirm?.callId === action.callId ? { ...state, confirm: null } : state;

    case 'error':
      return { ...state, error: action.message };
  }
}
