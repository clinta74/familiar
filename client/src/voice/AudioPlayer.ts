import type { SoundEffect } from '../protocol/generated';
import { playSoundEffect } from './soundEffects';
import { visemeAt, type TimedWord, type Viseme } from '../avatar/visemes';

/** A spoken sentence announced by the server (the `audio` event). */
export interface Clip {
  replyId: string;
  seq: number;
  url: string;
  /** What it says. */
  text: string;
  /** When each word is spoken, if the TTS service said. */
  words?: TimedWord[] | null;
}

/** How much of a reply the user has actually heard. */
export interface Heard {
  replyId: string;
  text: string;
}

interface Playing {
  clip: Clip;
  startedAt: number;
  duration: number;
}

/**
 * Plays a reply's sentences in order as they arrive, fetching each one as soon as it's announced so
 * the next is decoded before the current one ends (PLAN.md §3.2). While playing it reports, every
 * frame, how loud the voice is and which mouth shape is being made - and it keeps track of how much
 * of the reply the user has heard, which is what an interruption cuts the stored reply down to.
 *
 * Web Audio rather than <audio> elements: gapless playback, an exact clock for word timings, and an
 * AnalyserNode for the level.
 */
export class AudioPlayer {
  /** Called every animation frame while speaking: loudness 0-1, and the mouth shape (null between words). */
  onFrame?: (level: number, viseme: Viseme | null, hasTimings: boolean) => void;
  onPlayingChange?: (playing: boolean) => void;

  private context: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private samples: Float32Array<ArrayBuffer> | null = null;

  private replyId: string | null = null;
  private nextSeq = 0;
  private readonly decoded = new Map<number, { clip: Clip; buffer: Promise<AudioBuffer | null> }>();
  private source: AudioBufferSourceNode | null = null;
  private current: Playing | null = null;
  private finished: string[] = [];
  private playing = false;
  private frame = 0;

  /**
   * Browsers only let audio start after a user gesture. Call from a click or key handler (send,
   * talk, the voice toggle) so replies can play later.
   */
  unlock(): void {
    if (!this.context) {
      this.context = new AudioContext();
      this.analyser = this.context.createAnalyser();
      this.analyser.fftSize = 1024;
      this.analyser.connect(this.context.destination);
      this.samples = new Float32Array(this.analyser.fftSize);
    }
    if (this.context.state === 'suspended') void this.context.resume();
  }

  /**
   * A soft two-note chime, for a timer or reminder going off. Synthesised, so there's no file to
   * fetch; silent until the page has been interacted with (see {@link unlock}).
   */
  chime(): void {
    const context = this.context;
    if (!context || context.state !== 'running') return;

    const start = context.currentTime + 0.02;
    [880, 1318.5].forEach((frequency, i) => {
      const at = start + i * 0.22;
      const tone = context.createOscillator();
      const gain = context.createGain();
      tone.type = 'sine';
      tone.frequency.value = frequency;
      gain.gain.setValueAtTime(0, at);
      gain.gain.linearRampToValueAtTime(0.18, at + 0.015);
      gain.gain.exponentialRampToValueAtTime(0.0001, at + 0.9);
      tone.connect(gain).connect(context.destination);
      tone.start(at);
      tone.stop(at + 0.95);
    });
  }

  /** A sound effect, if the page may make sound yet (straight to the speakers: it doesn't move the mouth). */
  effect(sound: SoundEffect): void {
    if (this.context?.state === 'running') playSoundEffect(this.context, sound);
  }

  get isPlaying(): boolean {
    return this.playing;
  }

  /** The browser lets it make sound: something on the page was clicked, tapped or typed since it loaded. */
  get unlocked(): boolean {
    return this.context?.state === 'running';
  }

  enqueue(clip: Clip): void {
    if (clip.replyId !== this.replyId) {
      // A new reply supersedes whatever was still being said.
      this.stop();
      this.replyId = clip.replyId;
      this.nextSeq = 0;
      this.finished = [];
    }

    if (!this.decoded.has(clip.seq)) this.decoded.set(clip.seq, { clip, buffer: this.load(clip.url) });
    void this.pump();
  }

  /**
   * What the user has heard of the reply being spoken: every finished sentence, plus the words of
   * the current one that have been said. Null if nothing is being spoken.
   */
  heard(): Heard | null {
    // Nothing playing and nothing queued: the reply was heard to the end (or not at all), so
    // stopping the next one, still being thought about, mustn't cut this one.
    if (!this.replyId || (!this.current && (this.finished.length === 0 || this.decoded.size === 0))) return null;

    const parts = [...this.finished];
    if (this.current && this.context) {
      const elapsed = this.context.currentTime - this.current.startedAt;
      const { clip, duration } = this.current;
      if (clip.words?.length) {
        const said = clip.words.filter((w) => w.end <= elapsed + 0.05).map((w) => w.word);
        parts.push(joinWords(said));
      } else {
        // No timings: assume the words are spread evenly through the clip.
        const share = Math.min(1, Math.max(0, elapsed / Math.max(0.01, duration)));
        const cut = clip.text.lastIndexOf(' ', Math.floor(clip.text.length * share));
        if (cut > 0) parts.push(clip.text.slice(0, cut));
      }
    }

    return { replyId: this.replyId, text: parts.filter(Boolean).join(' ').trim() };
  }

  /** Silence, now: the user interrupted, started talking, or the conversation was cleared. */
  stop(): void {
    this.replyId = null;
    this.decoded.clear();
    this.current = null;
    this.finished = [];
    if (this.source) {
      this.source.onended = null;
      try {
        this.source.stop();
      } catch {
        // Already stopped.
      }
      this.source = null;
    }
    this.setPlaying(false);
  }

  private async load(url: string): Promise<AudioBuffer | null> {
    try {
      const response = await fetch(url, { credentials: 'same-origin' });
      if (!response.ok || !this.context) return null;
      return await this.context.decodeAudioData(await response.arrayBuffer());
    } catch {
      return null;
    }
  }

  private async pump(): Promise<void> {
    if (this.source || !this.context || !this.analyser) return;

    const reply = this.replyId;
    const pending = this.decoded.get(this.nextSeq);
    if (!pending) {
      // Nothing more announced yet; the next `audio` event resumes playback.
      this.setPlaying(false);
      return;
    }

    const buffer = await pending.buffer;
    if (reply !== this.replyId || this.source) return;

    this.decoded.delete(this.nextSeq);
    this.nextSeq++;
    if (!buffer) {
      void this.pump();
      return;
    }

    const source = this.context.createBufferSource();
    source.buffer = buffer;
    source.connect(this.analyser);
    source.onended = () => {
      if (this.source !== source) return;
      this.finished.push(pending.clip.text);
      this.source = null;
      this.current = null;
      void this.pump();
    };
    this.source = source;
    this.current = { clip: pending.clip, startedAt: this.context.currentTime, duration: buffer.duration };
    source.start();
    this.setPlaying(true);
  }

  private setPlaying(playing: boolean) {
    if (playing === this.playing) return;
    this.playing = playing;
    this.onPlayingChange?.(playing);

    cancelAnimationFrame(this.frame);
    if (playing) {
      const measure = () => {
        if (this.analyser && this.samples && this.context) {
          this.analyser.getFloatTimeDomainData(this.samples);
          let sum = 0;
          for (const s of this.samples) sum += s * s;
          const level = Math.min(1, Math.sqrt(sum / this.samples.length) * 5);

          const words = this.current?.clip.words;
          const viseme = words?.length && this.current
            ? visemeAt(words, this.context.currentTime - this.current.startedAt)
            : null;
          this.onFrame?.(level, viseme, !!words?.length);
        }
        this.frame = requestAnimationFrame(measure);
      };
      this.frame = requestAnimationFrame(measure);
    } else {
      this.onFrame?.(0, null, false);
    }
  }
}

/** Kokoro times punctuation as words of its own; put it back against the word before. */
function joinWords(words: string[]): string {
  return words.reduce((text, w) => (/^[.,!?;:…'"”’)]/.test(w) ? text + w : text ? `${text} ${w}` : w), '');
}
