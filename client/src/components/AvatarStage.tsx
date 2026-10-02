import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react';
import { createAvatar } from '../avatar/createAvatar';
import type { AvatarDriver, AvatarState, Emotion, Gesture } from '../avatar/types';

export interface AvatarHandle {
  express(emotion: Emotion, intensity: number): void;
  gesture(gesture: Gesture): void;
  /** A piece of speech arrived; moves the mouth in proportion to how much was said. */
  speak(text: string): void;
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
    speak: (text) => {
      // No audio until phase 2, so the mouth follows the text: a pulse per fragment, bigger for
      // longer ones, quieter at punctuation.
      const words = text.trim().length;
      if (words === 0) return;
      const level = /[.,!?;:]$/.test(text.trim()) ? 0.35 : Math.min(1, 0.45 + words / 12);
      driver.current?.setMouthOpen(level);
    },
  }), []);

  return <div ref={container} className="avatar-stage" data-state={state} />;
});
