import type { MicVAD } from '@ricky0123/vad-web';
import { encodeWav } from './wav';

export interface HandsFreeEvents {
  /** The user has started talking (sustained speech, not a cough). */
  onSpeechStart: () => void;
  /** They've stopped: the whole utterance, as WAV. */
  onSpeech: (utterance: Blob) => void;
  /** It looked like speech but was too short to be any. */
  onMisfire?: () => void;
}

// Silero VAD (v5) settings. Normal listening: patient enough not to cut someone off mid-pause.
const listening = { positiveSpeechThreshold: 0.6, negativeSpeechThreshold: 0.45, redemptionMs: 700, minSpeechMs: 300 };

// While the familiar is talking, the microphone also hears it from the speakers. Echo cancellation
// removes most of that; these make what leaks through much less likely to count as the user, at the
// cost of needing to speak up a little to interrupt. (The server also ignores a transcript that is
// just the familiar's own words.)
const overTheFamiliar = { positiveSpeechThreshold: 0.85, negativeSpeechThreshold: 0.7, redemptionMs: 700, minSpeechMs: 450 };

/**
 * Hands-free listening (PLAN.md §3.2): the browser runs Silero VAD on the microphone and hands over
 * each utterance when the user stops talking - no button. The model and ONNX runtime (~17 MB) load
 * only when this is switched on.
 */
export class HandsFree {
  static get supported(): boolean {
    return !!navigator.mediaDevices?.getUserMedia && typeof AudioWorkletNode !== 'undefined';
  }

  private vad: MicVAD | null = null;

  get active(): boolean {
    return this.vad !== null;
  }

  /** Starts listening. Asks for the microphone the first time. */
  async start(events: HandsFreeEvents): Promise<void> {
    if (this.vad) return;
    const { MicVAD } = await import('@ricky0123/vad-web');
    this.vad = await MicVAD.new({
      model: 'v5',
      baseAssetPath: '/vad/',
      onnxWASMBasePath: '/vad/',
      ...listening,
      preSpeechPadMs: 300,
      getStream: () =>
        navigator.mediaDevices.getUserMedia({
          audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 },
        }),
      onSpeechRealStart: () => events.onSpeechStart(),
      onSpeechEnd: (audio) => events.onSpeech(encodeWav(audio, 16000)),
      onVADMisfire: () => events.onMisfire?.(),
    });
    await this.vad.start();
  }

  /** Tell the listener whether the familiar is talking (see `overTheFamiliar`). */
  setFamiliarSpeaking(speaking: boolean): void {
    this.vad?.setOptions(speaking ? overTheFamiliar : listening);
  }

  async stop(): Promise<void> {
    const vad = this.vad;
    this.vad = null;
    await vad?.destroy();
  }
}
