import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import type { ChatMessage } from '../conversation/reducer';

export interface VoiceControls {
  /** Speech recognition and synthesis are reachable, and this browser can record. */
  available: boolean;
  /** Replies are spoken on this device. */
  on: boolean;
  recording: boolean;
  onToggle: () => void;
  onMic: () => void;
  handsFree: {
    available: boolean;
    on: boolean;
    /** The user is talking right now (hands-free heard them start). */
    hearing: boolean;
    onToggle: () => void;
  };
}

interface Props {
  familiarName: string;
  messages: ChatMessage[];
  /** Thinking or speaking: offer Stop instead of Send. */
  busy: boolean;
  error: string | null;
  onSend: (text: string) => Promise<void>;
  onInterrupt: () => void;
  onStartOver: () => void;
  onTyping: (typing: boolean) => void;
  voice: VoiceControls;
}

const emotionLabels: Record<string, string> = {
  joy: 'happy',
  sad: 'sad',
  angry: 'cross',
  surprised: 'surprised',
  relaxed: 'relaxed',
  thinking: 'thoughtful',
};

function MicIcon() {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
      <rect x="9" y="3" width="6" height="11" rx="3" fill="currentColor" />
      <path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21" stroke="currentColor" strokeWidth="2" fill="none" strokeLinecap="round" />
    </svg>
  );
}

function SpeakerIcon({ on }: { on: boolean }) {
  return (
    <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
      <path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z" fill="currentColor" />
      {on
        ? <path d="M15.5 9a4 4 0 0 1 0 6M18 6.5a7.5 7.5 0 0 1 0 11" stroke="currentColor" strokeWidth="1.8" fill="none" strokeLinecap="round" />
        : <path d="M16 9.5l5 5M21 9.5l-5 5" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />}
    </svg>
  );
}

export function ChatPanel({ familiarName, messages, busy, error, onSend, onInterrupt, onStartOver, onTyping, voice }: Props) {
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const transcript = useRef<HTMLOListElement>(null);

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
          <li className="transcript-empty muted">
            Say hello to {familiarName}{voice.handsFree.on ? ' — just talk.' : voice.available ? ' — type, or press the microphone and talk.' : '.'}
          </li>
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
        {voice.handsFree.on ? (
          <span
            className={`mic hands-free${voice.handsFree.hearing ? ' hearing' : ''}`}
            role="status"
            aria-label={voice.handsFree.hearing ? 'Hearing you' : 'Hands-free: listening'}
            title="Hands-free: just talk"
          >
            <MicIcon />
          </span>
        ) : voice.available && (
          <button
            type="button"
            className={`mic${voice.recording ? ' recording' : ''}`}
            onClick={voice.onMic}
            aria-pressed={voice.recording}
            aria-label={voice.recording ? 'Done talking — send' : `Talk to ${familiarName}`}
            title={voice.recording ? 'Done talking (Esc to cancel)' : 'Talk'}
          >
            <MicIcon />
          </button>
        )}
        <textarea
          value={draft}
          onChange={(e) => {
            setDraft(e.target.value);
            onTyping(e.target.value.length > 0);
          }}
          onKeyDown={onKeyDown}
          onBlur={() => onTyping(false)}
          placeholder={voice.recording ? 'Listening… press the microphone again when you’re done' : `Message ${familiarName}…`}
          rows={1}
          maxLength={4000}
          aria-label="Message"
          disabled={voice.recording}
        />
        {busy && !voice.recording ? (
          <button type="button" className="secondary" onClick={onInterrupt}>Stop</button>
        ) : (
          <button type="submit" disabled={!draft.trim() || sending || voice.recording}>Send</button>
        )}
      </form>

      <div className="chat-footer">
        <span className="voice-toggles">
          {voice.available && (
            <button type="button" className="link voice-toggle" onClick={voice.onToggle} aria-pressed={voice.on}>
              <SpeakerIcon on={voice.on} />
              {voice.on ? 'Voice on' : 'Voice off'}
            </button>
          )}
          {voice.handsFree.available && (
            <button type="button" className="link voice-toggle" onClick={voice.handsFree.onToggle} aria-pressed={voice.handsFree.on}>
              <MicIcon />
              {voice.handsFree.on ? 'Hands-free on' : 'Hands-free off'}
            </button>
          )}
        </span>
        <button type="button" className="link" onClick={onStartOver} disabled={messages.length === 0}>
          New conversation
        </button>
      </div>
    </section>
  );
}
