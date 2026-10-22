import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import type { LinkView, ToolConfirmEvent } from '../conversation/events';
import { isPicture, pictureUrl } from '../conversation/pictures';
import type { ChatMessage, ToolChip } from '../conversation/reducer';

export interface VoiceControls {
  /** Speech recognition and synthesis are reachable, and this browser can record. */
  available: boolean;
  /** Replies are spoken on this device. */
  on: boolean;
  recording: boolean;
  onToggle: () => void;
  onMic: () => void;
  /** Sound effects (a pop, a fanfare) besides the voice. */
  effects: { on: boolean; onToggle: () => void };
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
  /** Send what's typed, with a picture if one was added. */
  onSend: (text: string, picture?: Blob) => Promise<void>;
  onInterrupt: () => void;
  onStartOver: () => void;
  onTyping: (typing: boolean) => void;
  voice: VoiceControls;
  /** A tool waiting for the user's say-so. */
  confirm: ToolConfirmEvent | null;
  onConfirm: (callId: string, approve: boolean) => void;
}

const emotionLabels: Record<string, string> = {
  joy: 'happy',
  sad: 'sad',
  angry: 'cross',
  surprised: 'surprised',
  relaxed: 'relaxed',
  thinking: 'thoughtful',
};

/** What a tool is doing, while it does it. */
const toolDoing: Record<string, string> = {
  set_timer: 'Setting a timer',
  create_reminder: 'Making a reminder',
  list_scheduled: 'Checking your timers',
  cancel_scheduled: 'Cancelling',
};

/**
 * Pages the familiar handed over (BACKLOG item 34): a card each, opened in a new tab. They come from
 * its tools, never written by the model; and only http(s) is ever a link.
 */
function LinkCards({ links }: { links: LinkView[] }) {
  const safe = links.filter((l) => /^https?:\/\//i.test(l.url));
  if (safe.length === 0) return null;
  return (
    <ul className="link-cards" aria-label="Pages">
      {safe.map((l) => (
        <li key={l.url}>
          <a href={l.url} target="_blank" rel="noopener noreferrer">
            <strong>{l.title}</strong>
            <span>{l.site}</span>
          </a>
        </li>
      ))}
    </ul>
  );
}

function Chip({ chip }: { chip: ToolChip }) {
  const doing = toolDoing[chip.tool] ?? chip.tool.replace(/_/g, ' ');
  const text = chip.running ? `${doing}…` : chip.summary ?? doing;
  const status = chip.running ? 'running' : chip.ok ? 'done' : 'failed';
  return <li className={`tool-chip ${status}`}>{text}</li>;
}

function ConfirmCard({ confirm, onConfirm }: { confirm: ToolConfirmEvent; onConfirm: Props['onConfirm'] }) {
  return (
    <div className="confirm card" role="alertdialog" aria-label="Allow this?">
      <p>{confirm.description}?</p>
      <div className="confirm-actions">
        <button type="button" onClick={() => onConfirm(confirm.callId, true)}>Allow</button>
        <button type="button" className="secondary" onClick={() => onConfirm(confirm.callId, false)}>Don’t</button>
      </div>
    </div>
  );
}

// The round buttons' icons - a picture, the microphone, a headset for hands-free - in one outline style.
function MicIcon() {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round">
      <rect x="9" y="3" width="6" height="11" rx="3" />
      <path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21" />
    </svg>
  );
}

function HeadsetIcon() {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round">
      <path d="M4.5 15v-3a7.5 7.5 0 0 1 15 0v3" strokeLinecap="round" />
      <rect x="3.5" y="13.5" width="4" height="6.5" rx="1.6" />
      <rect x="16.5" y="13.5" width="4" height="6.5" rx="1.6" />
    </svg>
  );
}

function SparkleIcon() {
  return (
    <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
      <path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8zM18.5 15l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8z" fill="currentColor" />
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

function PictureIcon() {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round">
      <rect x="3.5" y="5" width="17" height="14" rx="2.5" />
      <circle cx="9" cy="10" r="1.6" />
      <path d="M4 17l5-5 4 4 2.5-2.5L20 17" strokeLinecap="round" />
    </svg>
  );
}

/** The picture about to be sent, small, with a way to take it back. */
function PicturePreview({ picture, onRemove }: { picture: File; onRemove: () => void }) {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    const made = URL.createObjectURL(picture);
    setUrl(made);
    return () => URL.revokeObjectURL(made);
  }, [picture]);
  return (
    <div className="picture-preview">
      {url && <img src={url} alt="The picture you're about to send" />}
      <span className="muted small">Goes with your message. Its location and camera details aren't sent.</span>
      <button type="button" className="link" onClick={onRemove}>Remove</button>
    </div>
  );
}

export function ChatPanel({
  familiarName, messages, busy, error, onSend, onInterrupt, onStartOver, onTyping, voice, confirm, onConfirm,
}: Props) {
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [picture, setPicture] = useState<File | null>(null);
  const pictureInput = useRef<HTMLInputElement>(null);
  const transcript = useRef<HTMLOListElement>(null);

  // Follow the conversation, unless the user has scrolled up to read. A conversation that arrives
  // all at once - restored on opening, reloaded after a resync, a different one - opens at its
  // latest message instead: the view starts at the top then, and would otherwise stay there.
  const lastText = messages.at(-1)?.text;
  const firstId = messages[0]?.id;
  const seen = useRef<{ count: number; firstId?: string }>({ count: 0 });
  useEffect(() => {
    const list = transcript.current;
    if (!list) return;
    const restored = messages.length - seen.current.count > 2 || firstId !== seen.current.firstId;
    seen.current = { count: messages.length, firstId };
    const nearBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 120;
    if (restored || nearBottom) list.scrollTop = list.scrollHeight;
  }, [messages.length, lastText, firstId]);

  async function submit(event?: FormEvent) {
    event?.preventDefault();
    const text = draft.trim();
    if ((!text && !picture) || sending) return;
    setSending(true);
    try {
      await onSend(text, picture ?? undefined);
      setDraft('');
      setPicture(null);
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
          <li key={m.id} className={`bubble ${m.role}${m.streaming ? ' streaming' : ''}${m.text ? '' : ' tools-only'}`}>
            {m.tools && m.tools.length > 0 && (
              <ul className="tool-chips" aria-label="What it did">
                {m.tools.map((chip) => <Chip key={chip.callId} chip={chip} />)}
              </ul>
            )}
            {m.imageId && <img className="bubble-picture" src={pictureUrl(m.imageId)} alt="A picture you sent" loading="lazy" />}
            {(m.text || (!m.tools?.length && !m.imageId)) && <span className="bubble-text">{m.text}{m.interrupted && '…'}</span>}
            {m.links && m.links.length > 0 && <LinkCards links={m.links} />}
            {m.role === 'assistant' && m.emotion && emotionLabels[m.emotion] && !m.streaming && (
              <span className="bubble-meta">{emotionLabels[m.emotion]}</span>
            )}
          </li>
        ))}
      </ol>

      {confirm && <ConfirmCard confirm={confirm} onConfirm={onConfirm} />}

      {error && <p className="error chat-error" role="alert">{error}</p>}

      {picture && <PicturePreview picture={picture} onRemove={() => setPicture(null)} />}

      <form
        className="composer"
        onSubmit={submit}
        onDragOver={(e) => { if ([...e.dataTransfer.items].some((i) => i.type.startsWith('image/'))) e.preventDefault(); }}
        onDrop={(e) => {
          const dropped = [...e.dataTransfer.files].find(isPicture);
          if (dropped) { e.preventDefault(); setPicture(dropped); }
        }}
      >
        <textarea
          onPaste={(e) => {
            const pasted = [...e.clipboardData.files].find(isPicture);
            if (pasted) { e.preventDefault(); setPicture(pasted); }
          }}
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
          <button type="submit" disabled={(!draft.trim() && !picture) || sending || voice.recording} aria-busy={sending}>Send</button>
        )}
      </form>

      {/* Under the message box, which has its line to itself: the picture, talking and hands-free,
          alike; then the voice and sound switches (icons, said in their tooltips) and starting over -
          one line in the chat panel's width. */}
      <div className="chat-actions">
        <span className="round-buttons">
          <button
            type="button"
            className="round"
            onClick={() => pictureInput.current?.click()}
            aria-label="Add a picture"
            title="Add a picture (or paste one)"
            disabled={voice.recording || sending}
          >
            <PictureIcon />
          </button>
          <input
            ref={pictureInput}
            id="chat-picture"
            type="file"
            accept="image/*"
            hidden
            onChange={(e) => {
              const chosen = e.target.files?.[0];
              if (chosen && isPicture(chosen)) setPicture(chosen);
              e.target.value = '';
            }}
          />
          {voice.handsFree.on ? (
            <span
              className={`round ringed${voice.handsFree.hearing ? ' hearing' : ''}`}
              role="status"
              aria-label={voice.handsFree.hearing ? 'Hearing you' : 'Hands-free: listening'}
              title="Hands-free: just talk"
            >
              <MicIcon />
            </span>
          ) : voice.available && (
            <button
              type="button"
              className={`round${voice.recording ? ' recording' : ''}`}
              onClick={voice.onMic}
              aria-pressed={voice.recording}
              aria-label={voice.recording ? 'Done talking — send' : `Talk to ${familiarName}`}
              title={voice.recording ? 'Done talking (Esc to cancel)' : 'Talk'}
            >
              <MicIcon />
            </button>
          )}
          {voice.handsFree.available && (
            <>
              <button
                type="button"
                className="round"
                onClick={voice.handsFree.onToggle}
                aria-pressed={voice.handsFree.on}
                aria-label="Hands-free"
                title={voice.handsFree.on ? 'Hands-free is on: turn it off' : 'Hands-free: talk without pressing anything'}
              >
                <HeadsetIcon />
              </button>
              {voice.handsFree.on && <span className="muted hands-free-note">Hands-free: just talk.</span>}
            </>
          )}
        </span>
        <span className="voice-toggles">
          {voice.available && (
            <button
              type="button"
              className="link voice-toggle"
              onClick={voice.onToggle}
              aria-pressed={voice.on}
              aria-label="Voice"
              title={voice.on ? 'Voice on: it speaks its replies' : 'Voice off: replies in text only'}
            >
              <SpeakerIcon on={voice.on} />
            </button>
          )}
          <button
            type="button"
            className="link voice-toggle"
            onClick={voice.effects.onToggle}
            aria-pressed={voice.effects.on}
            aria-label="Sounds"
            title={voice.effects.on ? 'Sounds on: drumrolls, chimes and the like' : 'Sounds off'}
          >
            <SparkleIcon />
          </button>
          <button type="button" className="link" onClick={onStartOver} disabled={messages.length === 0}>
            New conversation
          </button>
        </span>
      </div>
    </section>
  );
}
