import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import type { ChatMessage } from '../conversation/reducer';
import type { FamiliarState } from '../conversation/events';

interface Props {
  familiarName: string;
  messages: ChatMessage[];
  state: FamiliarState;
  error: string | null;
  onSend: (text: string) => Promise<void>;
  onInterrupt: () => void;
  onStartOver: () => void;
  onTyping: (typing: boolean) => void;
}

const emotionLabels: Record<string, string> = {
  joy: 'happy',
  sad: 'sad',
  angry: 'cross',
  surprised: 'surprised',
  relaxed: 'relaxed',
  thinking: 'thoughtful',
};

export function ChatPanel({ familiarName, messages, state, error, onSend, onInterrupt, onStartOver, onTyping }: Props) {
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const transcript = useRef<HTMLOListElement>(null);
  const busy = state === 'thinking' || state === 'speaking';

  // Follow the conversation, unless the user has scrolled up to read.
  const lastText = messages.at(-1)?.text;
  useEffect(() => {
    const list = transcript.current;
    if (!list) return;
    const nearBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 120;
    if (nearBottom) list.scrollTop = list.scrollHeight;
  }, [messages.length, lastText]);

  async function submit(event?: FormEvent) {
    event?.preventDefault();
    const text = draft.trim();
    if (!text || sending) return;
    setSending(true);
    try {
      await onSend(text);
      setDraft('');
      onTyping(false);
    } catch {
      // The error is shown from conversation state; keep the draft so it can be resent.
    } finally {
      setSending(false);
    }
  }

  function onKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault();
      void submit();
    }
  }

  return (
    <section className="chat card" aria-label={`Conversation with ${familiarName}`}>
      <ol className="transcript" ref={transcript} aria-live="polite">
        {messages.length === 0 && (
          <li className="transcript-empty muted">Say hello to {familiarName}.</li>
        )}
        {messages.map((m) => (
          <li key={m.id} className={`bubble ${m.role}${m.streaming ? ' streaming' : ''}`}>
            <span className="bubble-text">{m.text}{m.interrupted && '…'}</span>
            {m.role === 'assistant' && m.emotion && emotionLabels[m.emotion] && !m.streaming && (
              <span className="bubble-meta">{emotionLabels[m.emotion]}</span>
            )}
          </li>
        ))}
      </ol>

      {error && <p className="error chat-error" role="alert">{error}</p>}

      <form className="composer" onSubmit={submit}>
        <textarea
          value={draft}
          onChange={(e) => {
            setDraft(e.target.value);
            onTyping(e.target.value.length > 0);
          }}
          onKeyDown={onKeyDown}
          onBlur={() => onTyping(false)}
          placeholder={`Message ${familiarName}…`}
          rows={1}
          maxLength={4000}
          aria-label="Message"
        />
        {busy ? (
          <button type="button" className="secondary" onClick={onInterrupt}>Stop</button>
        ) : (
          <button type="submit" disabled={!draft.trim() || sending}>Send</button>
        )}
      </form>

      <div className="chat-footer">
        <button type="button" className="link" onClick={onStartOver} disabled={messages.length === 0}>
          New conversation
        </button>
      </div>
    </section>
  );
}
