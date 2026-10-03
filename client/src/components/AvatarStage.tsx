import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react';
import { defaultAppearance, type Appearance } from '../avatar/appearance';
import { createAvatar } from '../avatar/createAvatar';
import type { AvatarDriver, AvatarState, Emotion, Gesture } from '../avatar/types';
import type { Viseme } from '../avatar/visemes';

export interface AvatarHandle {
  express(emotion: Emotion, intensity: number): void;
  gesture(gesture: Gesture): void;
  /** A piece of reply text arrived and there's no audio: moves the mouth with the text. */
  speak(text: string): void;
  /** Loudness of the voice being played, 0-1, every frame: moves the mouth with the audio. */
  mouth(level: number): void;
  /** The mouth shape being spoken (from word timings) at a loudness, every frame. */
  viseme(viseme: Viseme | null, level: number): void;
}

/**
 * Hosts whichever avatar renderer is available and feeds it. Expression and gesture events go
 * straight to the driver through the handle, not through React state: they're moments, not data.
 */
export const AvatarStage = forwardRef<AvatarHandle, {
  state: AvatarState;
  appearance?: Appearance | null;
  /** How far a 3D character is turned round, in radians (the studio's turn control). */
  turn?: number;
}>(function AvatarStage({ state, appearance, turn = 0 }, ref) {
  const container = useRef<HTMLDivElement>(null);
  const driver = useRef<AvatarDriver | null>(null);
  const stateRef = useRef(state);
  const turnRef = useRef(turn);
  const look = appearance ?? defaultAppearance;
  const lookRef = useRef(look);

  // A different model means a different renderer; anything else is applied to the one there is.
  const model = look.model === 'vrm' ? (look.vrmId ? `vrm:${look.vrmId}` : 'llama') : `${look.model}:${look.style ?? '2d'}`;

  // First, so a change of model below builds the new renderer from the new look, not the last one.
  useEffect(() => {
    lookRef.current = look;
    driver.current?.setLook?.(look);
  }, [look]);

  useEffect(() => {
    let disposed = false;
    let created: AvatarDriver | null = null;

    void createAvatar(container.current!, lookRef.current).then((avatar) => {
      if (disposed) {
        avatar.dispose();
        return;
      }
      created = avatar;
      driver.current = avatar;
      avatar.setState(stateRef.current);
      avatar.setTurn?.(turnRef.current);
    });

    return () => {
      disposed = true;
      created?.dispose();
      driver.current = null;
    };
  }, [model]);

  useEffect(() => {
    stateRef.current = state;
    driver.current?.setState(state);
  }, [state]);

  useEffect(() => {
    turnRef.current = turn;
    driver.current?.setTurn?.(turn);
  }, [turn]);

  useImperativeHandle(ref, () => ({
    express: (emotion, intensity) => driver.current?.setExpression(emotion, intensity),
    gesture: (gesture) => driver.current?.playGesture(gesture),
    mouth: (level) => driver.current?.setMouthOpen(level),
    viseme: (viseme, level) => driver.current?.setViseme(viseme, level),
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
