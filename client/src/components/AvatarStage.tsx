import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react';
import { createAvatar } from '../avatar/createAvatar';
import type { AvatarDriver, AvatarState, Emotion, Gesture } from '../avatar/types';

export interface AvatarHandle {
  express(emotion: Emotion, intensity: number): void;
  gesture(gesture: Gesture): void;
  /** A piece of reply text arrived and there's no audio: moves the mouth with the text. */
  speak(text: string): void;
  /** Loudness of the voice being played, 0-1, every frame: moves the mouth with the audio. */
  mouth(level: number): void;
}

/**
 * Hosts whichever avatar renderer is available and feeds it. Expression and gesture events go
 * straight to the driver through the handle, not through React state: they're moments, not data.
 */
export const AvatarStage = forwardRef<AvatarHandle, { state: AvatarState }>(function AvatarStage({ state }, ref) {
  const container = useRef<HTMLDivElement>(null);
  const driver = useRef<AvatarDriver | null>(null);
  const stateRef = useRef(state);

  useEffect(() => {
    let disposed = false;
    let created: AvatarDriver | null = null;

    void createAvatar(container.current!).then((avatar) => {
      if (disposed) {
        avatar.dispose();
        return;
      }
      created = avatar;
      driver.current = avatar;
      avatar.setState(stateRef.current);
    });

    return () => {
      disposed = true;
      created?.dispose();
      driver.current = null;
    };
  }, []);

  useEffect(() => {
    stateRef.current = state;
    driver.current?.setState(state);
  }, [state]);

  useImperativeHandle(ref, () => ({
    express: (emotion, intensity) => driver.current?.setExpression(emotion, intensity),
    gesture: (gesture) => driver.current?.playGesture(gesture),
    mouth: (level) => driver.current?.setMouthOpen(level),
    speak: (text) => {
      // Without audio the mouth follows the text: a pulse per fragment, bigger for longer ones,
      // quieter at punctuation.
      const words = text.trim().length;
      if (words === 0) return;
      const level = /[.,!?;:]$/.test(text.trim()) ? 0.35 : Math.min(1, 0.45 + words / 12);
      driver.current?.setMouthOpen(level);
    },
  }), []);

  return <div ref={container} className="avatar-stage" data-state={state} />;
});
