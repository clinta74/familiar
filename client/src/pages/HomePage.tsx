import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { AvatarState } from '../avatar/types';
import { AvatarStage, type AvatarHandle } from '../components/AvatarStage';
import { ChatPanel } from '../components/ChatPanel';
import { TimersPanel } from '../components/TimersPanel';
import { familiarApi } from '../api/familiar';
import { defaultAppearance, type Appearance } from '../avatar/appearance';
import { cachedLook, rememberLook } from '../avatar/lookCache';
import { useAuth } from '../auth/AuthContext';
import { useConversation } from '../conversation/useConversation';
import { AudioPlayer, type Clip } from '../voice/AudioPlayer';
import { HandsFree } from '../voice/HandsFree';
import { Recorder } from '../voice/Recorder';
import { Shell } from './Shell';

const VOICE_KEY = 'familiar.voice';
const EFFECTS_KEY = 'familiar.effects';

// A per-device preference, so browser storage; it may be unavailable (private mode), which just
// means the default.
function readPreference(key: string): boolean {
  try {
    return localStorage.getItem(key) !== 'off';
  } catch {
    return true;
  }
}

function writePreference(key: string, on: boolean) {
  try {
    localStorage.setItem(key, on ? 'on' : 'off');
  } catch {
    // Not persisted; it still applies for this visit.
  }
}

const readVoicePreference = () => readPreference(VOICE_KEY);
const writeVoicePreference = (on: boolean) => writePreference(VOICE_KEY, on);

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
  const [effectsOn, setEffectsOn] = useState(() => readPreference(EFFECTS_KEY));
  const [handsFreeOn, setHandsFreeOn] = useState(false);
  const [userSpeaking, setUserSpeaking] = useState(false);
  const { account } = useAuth();
  const username = account?.username;
  // How it looked last time on this browser, until the profile says (null: nothing yet).
  const [appearance, setAppearance] = useState<Appearance | null>(() => cachedLook(username));

  // How it looks is the studio's business; here it's only read. If it can't be, the default.
  useEffect(() => {
    familiarApi
      .get()
      .then((p) => {
        // Always there when read; null only means "unchanged" when saving.
        const look = p.appearance ?? defaultAppearance;
        setAppearance(look);
        rememberLook(username, look);
      })
      .catch(() => setAppearance((a) => a ?? defaultAppearance));
  }, [username]);

  const player = useMemo(() => new AudioPlayer(), []);
  const recorder = useMemo(() => new Recorder(), []);
  const handsFree = useMemo(() => new HandsFree(), []);

  // Read by stream and microphone callbacks, which outlive renders.
  const voiceOnRef = useRef(voiceOn);
  const effectsOnRef = useRef(effectsOn);
  const audibleRef = useRef(audible);
  useEffect(() => {
    voiceOnRef.current = voiceOn;
    effectsOnRef.current = effectsOn;
    audibleRef.current = audible;
  });

  useEffect(() => {
    // With word timings the mouth makes the shapes of the words; without, it follows loudness.
    player.onFrame = (level, viseme, hasTimings) => {
      if (hasTimings) avatar.current?.viseme(viseme, level);
      else avatar.current?.mouth(level);
    };
    player.onPlayingChange = setAudible;
    return () => {
      player.stop();
      recorder.cancel();
      void handsFree.stop();
    };
  }, [player, recorder, handsFree]);

  // Browsers keep a page silent until it's been clicked, tapped or typed in. Any of those arms the
  // voice, not only sending or talking: an announcement from a hook can arrive before either.
  useEffect(() => {
    const arm = () => player.unlock();
    const options = { capture: true, once: true } as const;
    document.addEventListener('pointerdown', arm, options);
    document.addEventListener('keydown', arm, options);
    return () => {
      document.removeEventListener('pointerdown', arm, options);
      document.removeEventListener('keydown', arm, options);
    };
  }, [player]);

  // Left alone for a few minutes it dozes off; anyone tapping, typing or coming back to the page
  // wakes it (typing a message also makes it listen).
  useEffect(() => {
    const wake = () => avatar.current?.wake();
    const onVisible = () => {
      if (document.visibilityState === 'visible') wake();
    };
    const options = { capture: true, passive: true } as const;
    document.addEventListener('pointerdown', wake, options);
    document.addEventListener('keydown', wake, options);
    window.addEventListener('focus', wake);
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      document.removeEventListener('pointerdown', wake, options);
      document.removeEventListener('keydown', wake, options);
      window.removeEventListener('focus', wake);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, []);

  /** What was said aloud while the page couldn't play it yet: kept, and offered with a button. */
  const unheard = useRef<Clip[]>([]);
  const [hasUnheard, setHasUnheard] = useState(false);

  const hearUnheard = useCallback(() => {
    player.unlock();
    for (const clip of unheard.current) player.enqueue(clip);
    unheard.current = [];
    setHasUnheard(false);
  }, [player]);

  const conversation = useConversation({
    onExpression: (emotion, intensity) => avatar.current?.express(emotion, intensity),
    onGesture: (gesture) => avatar.current?.gesture(gesture),
    onSound: (sound) => {
      if (effectsOnRef.current) player.effect(sound);
    },
    // With audio playing, the mouth follows the voice; otherwise it follows the text.
    onSpeech: (text) => {
      if (!audibleRef.current) avatar.current?.speak(text);
    },
    onReplyStart: () => {
      player.stop();
      unheard.current = [];
      setHasUnheard(false);
    },
    onAudio: (clip) => {
      if (!voiceOnRef.current) return;
      if (player.unlocked) {
        player.enqueue(clip);
      } else {
        unheard.current.push(clip);
        setHasUnheard(true);
      }
    },
    onCleared: () => player.stop(),
    // The announcement itself arrives as a reply; this is the bell before it.
    onNotify: () => player.chime(),
  }, { speakGreeting: voiceOn });

  const busy = conversation.state === 'thinking' || conversation.state === 'speaking' || audible;
  const busyRef = useRef(busy);
  const conversationRef = useRef(conversation);
  useEffect(() => {
    busyRef.current = busy;
    conversationRef.current = conversation;
  });

  /**
   * The user talked over the familiar (PLAN.md §3.2): silence it now, stop the reply, and tell the
   * server how much of it was actually heard, so the stored reply matches what was said aloud.
   */
  const bargeIn = useCallback(() => {
    const heard = player.heard();
    player.stop();
    if (heard || busyRef.current) conversationRef.current.interrupt(heard);
  }, [player]);

  const canSpeak = conversation.speechAvailable && voiceOn;

  const send = useCallback(async (text: string) => {
    player.unlock();
    await conversation.send(text, canSpeak);
  }, [player, conversation, canSpeak]);

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
      if (busy) bargeIn();
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
  }, [recording, busy, bargeIn, recorder, player, conversation]);

  /** Hands-free: the microphone stays open and each utterance is sent when the user stops talking. */
  const toggleHandsFree = useCallback(async () => {
    setMicError(null);
    if (handsFree.active) {
      await handsFree.stop();
      setHandsFreeOn(false);
      setUserSpeaking(false);
      return;
    }

    player.unlock();
    try {
      await handsFree.start({
        onSpeechStart: () => {
          setUserSpeaking(true);
          if (busyRef.current) bargeIn();
        },
        onSpeech: (utterance) => {
          setUserSpeaking(false);
          void conversationRef.current.sendSpoken(utterance);
        },
        onMisfire: () => setUserSpeaking(false),
      });
      setHandsFreeOn(true);
    } catch {
      setMicError('Hands-free couldn’t start. Check that this page may use the microphone.');
    }
  }, [handsFree, player, bargeIn]);

  // Speaking over the familiar takes a firmer voice than speaking to a quiet room.
  useEffect(() => handsFree.setFamiliarSpeaking(audible), [handsFree, audible]);

  // Escape abandons a push-to-talk recording.
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

  const avatarState: AvatarState = recording || userSpeaking
    ? 'listening'
    : audible
      ? 'speaking'
      : conversation.state === 'idle' && typing
        ? 'listening'
        : conversation.state;

  const status = conversation.connection === 'reconnecting'
    ? 'reconnecting…'
    : statusText[avatarState] ?? (handsFreeOn ? 'hands-free — just talk' : undefined);

  return (
    <Shell>
      <div className="home">
        <section className="stage-panel" aria-label={conversation.familiarName}>
          <AvatarStage ref={avatar} state={avatarState} appearance={appearance} />
          <p className="familiar-name">
            {conversation.familiarName}
            {status && <span className="familiar-status"> · {status}</span>}
          </p>
          {hasUnheard && (
            <button type="button" className="secondary hear-unheard" onClick={hearUnheard}>
              🔊 Hear what {conversation.familiarName} said
            </button>
          )}
          <TimersPanel jobs={conversation.jobs} onCancel={conversation.cancelJob} />
        </section>
        <ChatPanel
          familiarName={conversation.familiarName}
          messages={conversation.messages}
          busy={busy}
          error={micError ?? conversation.error}
          onSend={send}
          onInterrupt={bargeIn}
          onStartOver={() => {
            player.stop();
            conversation.startOver();
          }}
          onTyping={setTyping}
          confirm={conversation.confirm}
          onConfirm={conversation.answerConfirm}
          voice={{
            available: conversation.speechAvailable && Recorder.supported,
            on: voiceOn,
            recording,
            onToggle: toggleVoice,
            onMic: () => void toggleMic(),
            effects: {
              on: effectsOn,
              onToggle: () => {
                player.unlock();
                setEffectsOn((on) => {
                  writePreference(EFFECTS_KEY, !on);
                  return !on;
                });
              },
            },
            handsFree: {
              available: conversation.speechAvailable && HandsFree.supported,
              on: handsFreeOn,
              hearing: userSpeaking,
              onToggle: () => void toggleHandsFree(),
            },
          }}
        />
      </div>
    </Shell>
  );
}
