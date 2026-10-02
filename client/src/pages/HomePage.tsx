import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { AvatarState } from '../avatar/types';
import { AvatarStage, type AvatarHandle } from '../components/AvatarStage';
import { ChatPanel } from '../components/ChatPanel';
import { useConversation } from '../conversation/useConversation';
import { AudioPlayer } from '../voice/AudioPlayer';
import { Recorder } from '../voice/Recorder';
import { Shell } from './Shell';

const VOICE_KEY = 'familiar.voice';

// A per-device preference, so browser storage; it may be unavailable (private mode), which just
// means the default.
function readVoicePreference(): boolean {
  try {
    return localStorage.getItem(VOICE_KEY) !== 'off';
  } catch {
    return true;
  }
}

function writeVoicePreference(on: boolean) {
  try {
    localStorage.setItem(VOICE_KEY, on ? 'on' : 'off');
  } catch {
    // Not persisted; it still applies for this visit.
  }
}

const statusText: Record<string, string> = {
  listening: 'listening…',
  thinking: 'thinking…',
  speaking: 'talking',
  resting: 'resting — the model isn’t reachable',
};

export function HomePage() {
  const avatar = useRef<AvatarHandle>(null);
  const [typing, setTyping] = useState(false);
  const [recording, setRecording] = useState(false);
  const [audible, setAudible] = useState(false);
  const [micError, setMicError] = useState<string | null>(null);
  const [voiceOn, setVoiceOn] = useState(readVoicePreference);

  const player = useMemo(() => new AudioPlayer(), []);
  const recorder = useMemo(() => new Recorder(), []);

  // Read by stream handlers, which outlive renders.
  const voiceOnRef = useRef(voiceOn);
  const audibleRef = useRef(audible);
  useEffect(() => {
    voiceOnRef.current = voiceOn;
    audibleRef.current = audible;
  });

  useEffect(() => {
    player.onLevel = (level) => avatar.current?.mouth(level);
    player.onPlayingChange = setAudible;
    return () => {
      player.stop();
      recorder.cancel();
    };
  }, [player, recorder]);

  const conversation = useConversation({
    onExpression: (emotion, intensity) => avatar.current?.express(emotion, intensity),
    onGesture: (gesture) => avatar.current?.gesture(gesture),
    // With audio playing, the mouth follows the voice; otherwise it follows the text.
    onSpeech: (text) => {
      if (!audibleRef.current) avatar.current?.speak(text);
    },
    onReplyStart: () => player.stop(),
    onAudio: (clip) => {
      if (voiceOnRef.current) player.enqueue(clip);
    },
    onCleared: () => player.stop(),
  });

  const busy = conversation.state === 'thinking' || conversation.state === 'speaking' || audible;
  const canSpeak = conversation.speechAvailable && voiceOn;

  const send = useCallback(async (text: string) => {
    player.unlock();
    await conversation.send(text, canSpeak);
  }, [player, conversation, canSpeak]);

  const interrupt = useCallback(() => {
    player.stop();
    conversation.interrupt();
  }, [player, conversation]);

  const toggleVoice = useCallback(() => {
    player.unlock();
    setVoiceOn((on) => {
      if (on) player.stop();
      writeVoicePreference(!on);
      return !on;
    });
  }, [player]);

  /** Push-to-talk: first press starts listening (and interrupts the familiar), second sends. */
  const toggleMic = useCallback(async () => {
    setMicError(null);

    if (!recording) {
      player.unlock();
      if (busy) interrupt();
      try {
        await recorder.start();
        setRecording(true);
      } catch {
        setMicError('I can’t use the microphone. Check that this page is allowed to.');
      }
      return;
    }

    setRecording(false);
    const utterance = await recorder.stop();
    if (utterance) await conversation.sendSpoken(utterance);
  }, [recording, busy, interrupt, recorder, player, conversation]);

  // Escape abandons a recording.
  useEffect(() => {
    if (!recording) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        recorder.cancel();
        setRecording(false);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [recording, recorder]);

  const avatarState: AvatarState = recording
    ? 'listening'
    : audible
      ? 'speaking'
      : conversation.state === 'idle' && typing
        ? 'listening'
        : conversation.state;

  const status = conversation.connection === 'reconnecting' ? 'reconnecting…' : statusText[avatarState];

  return (
    <Shell>
      <div className="home">
        <section className="stage-panel" aria-label={conversation.familiarName}>
          <AvatarStage ref={avatar} state={avatarState} />
          <p className="familiar-name">
            {conversation.familiarName}
            {status && <span className="familiar-status"> · {status}</span>}
          </p>
        </section>
        <ChatPanel
          familiarName={conversation.familiarName}
          messages={conversation.messages}
          busy={busy}
          error={micError ?? conversation.error}
          onSend={send}
          onInterrupt={interrupt}
          onStartOver={() => {
            player.stop();
            conversation.startOver();
          }}
          onTyping={setTyping}
          voice={{
            available: conversation.speechAvailable && Recorder.supported,
            on: voiceOn,
            recording,
            onToggle: toggleVoice,
            onMic: () => void toggleMic(),
          }}
        />
      </div>
    </Shell>
  );
}
