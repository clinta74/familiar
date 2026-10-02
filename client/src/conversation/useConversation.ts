import { useCallback, useEffect, useReducer, useRef } from 'react';
import type { Emotion, Gesture } from '../avatar/types';
import { conversationApi } from './conversationApi';
import type {
  AudioEvent,
  ErrorEvent,
  ReplyTruncatedEvent,
  ExpressionEvent,
  GestureEvent,
  JobsEvent,
  MessageView,
  NotifyEvent,
  ReadyEvent,
  ReplyDeltaEvent,
  ReplyEndEvent,
  ReplyStartEvent,
  StateEvent,
  ToolConfirmedEvent,
  ToolConfirmEvent,
  ToolFinishedEvent,
  ToolStartedEvent,
} from './events';
import { conversationReducer, initialConversation } from './reducer';

/** Things the avatar reacts to that don't belong in conversation state. */
export interface ConversationHandlers {
  onExpression?: (emotion: Emotion, intensity: number) => void;
  onGesture?: (gesture: Gesture) => void;
  /** Each piece of reply text as it arrives - what moves the mouth when there's no audio. */
  onSpeech?: (text: string) => void;
  /** A new reply has started: anything still being spoken from the last one should stop. */
  onReplyStart?: (id: string) => void;
  /** A spoken sentence is ready to play. */
  onAudio?: (clip: AudioEvent) => void;
  /** The conversation was cleared, on this device or another. */
  onCleared?: () => void;
  /** A timer or reminder went off. */
  onNotify?: (event: NotifyEvent) => void;
}

/**
 * The live conversation: history on load, then the event stream (PLAN.md §3.1). EventSource does
 * the reconnecting, and sends Last-Event-ID so the server replays what was missed.
 */
export function useConversation(handlers: ConversationHandlers, options: { speakGreeting?: boolean } = {}) {
  const [state, dispatch] = useReducer(conversationReducer, initialConversation);

  // The stream outlives renders; handlers are read through a ref so it never has to reconnect.
  const handlersRef = useRef(handlers);
  useEffect(() => {
    handlersRef.current = handlers;
  });

  useEffect(() => {
    let disposed = false;
    const loadHistory = () =>
      conversationApi
        .history()
        .then((messages) => !disposed && dispatch({ type: 'history', messages }))
        .catch(() => undefined);

    void loadHistory();

    // The time zone, so a greeting knows what "this morning" means; and whether to speak it.
    const query = new URLSearchParams({
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      speak: String(options.speakGreeting ?? false),
    });
    const source = new EventSource(`/api/conversation/stream?${query.toString()}`);
    const on = <T,>(type: string, handle: (payload: T) => void) =>
      source.addEventListener(type, (event) => handle(JSON.parse((event as MessageEvent<string>).data) as T));

    on<ReadyEvent>('ready', (ready) => dispatch({ type: 'ready', ready }));
    on<StateEvent>('state', ({ state }) => dispatch({ type: 'state', state }));
    on<MessageView>('message', (message) => dispatch({ type: 'message', message }));
    on<ReplyStartEvent>('reply.start', ({ id }) => {
      dispatch({ type: 'replyStart', id });
      handlersRef.current.onReplyStart?.(id);
    });
    on<ReplyDeltaEvent>('reply.delta', ({ id, text }) => {
      dispatch({ type: 'replyDelta', id, text });
      handlersRef.current.onSpeech?.(text);
    });
    on<ReplyEndEvent>('reply.end', (end) => dispatch({ type: 'replyEnd', end }));
    on<ExpressionEvent>('expression', ({ emotion, intensity }) => handlersRef.current.onExpression?.(emotion, intensity));
    on<GestureEvent>('gesture', ({ gesture }) => handlersRef.current.onGesture?.(gesture));
    on<ErrorEvent>('turn.error', ({ message }) => dispatch({ type: 'error', message }));
    on<AudioEvent>('audio', (clip) => handlersRef.current.onAudio?.(clip));
    on<ReplyTruncatedEvent>('reply.truncated', ({ id, text }) => dispatch({ type: 'replyTruncated', id, text }));
    on('cleared', () => {
      dispatch({ type: 'cleared' });
      handlersRef.current.onCleared?.();
    });
    on('resync', () => void loadHistory());
    on<JobsEvent>('jobs', ({ jobs }) => dispatch({ type: 'jobs', jobs }));
    on<NotifyEvent>('notify', (event) => handlersRef.current.onNotify?.(event));
    on<ToolStartedEvent>('tool.started', (event) => dispatch({ type: 'toolStarted', event }));
    on<ToolFinishedEvent>('tool.finished', (event) => dispatch({ type: 'toolFinished', event }));
    on<ToolConfirmEvent>('tool.confirm', (event) => dispatch({ type: 'confirm', event }));
    on<ToolConfirmedEvent>('tool.confirmed', ({ callId }) => dispatch({ type: 'confirmed', callId }));

    // The connection dropped. EventSource retries by itself; 'ready' marks it open again.
    source.onerror = () => {
      if (!disposed) dispatch({ type: 'connection', connection: 'reconnecting' });
    };

    return () => {
      disposed = true;
      source.close();
    };
    // Connect once: the options are read at mount; a change of voice setting needn't reconnect.
  }, []);

  const send = useCallback(async (text: string, speak: boolean) => {
    dispatch({ type: 'error', message: null });
    try {
      const message = await conversationApi.send(text, speak);
      dispatch({ type: 'message', message });
    } catch (error) {
      dispatch({ type: 'error', message: error instanceof Error ? error.message : 'Could not send that.' });
      throw error;
    }
  }, []);

  const sendSpoken = useCallback(async (recording: Blob) => {
    dispatch({ type: 'error', message: null });
    try {
      const message = await conversationApi.sendSpoken(recording);
      if (message) dispatch({ type: 'message', message });
    } catch (error) {
      dispatch({ type: 'error', message: error instanceof Error ? error.message : 'Could not send that.' });
    }
  }, []);

  /** Stop the reply. With `heard`, the stored reply is cut to what was actually heard of it. */
  const interrupt = useCallback(
    (heard?: { replyId: string; text: string } | null) =>
      void conversationApi.interrupt(heard ?? undefined).catch(() => undefined),
    [],
  );
  const startOver = useCallback(() => void conversationApi.startOver().catch(() => undefined), []);

  /** The jobs event that follows updates the list on every device. */
  const cancelJob = useCallback((id: string) => void conversationApi.cancelJob(id).catch(() => undefined), []);

  const answerConfirm = useCallback((callId: string, approve: boolean) => {
    dispatch({ type: 'confirmed', callId });
    void conversationApi.confirm(callId, approve).catch(() => undefined);
  }, []);

  return { ...state, send, sendSpoken, interrupt, startOver, cancelJob, answerConfirm };
}
