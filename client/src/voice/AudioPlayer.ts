/** A spoken sentence announced by the server (the `audio` event). */
export interface Clip {
  replyId: string;
  seq: number;
  url: string;
}

/**
 * Plays a reply's sentences in order as they arrive, fetching each one as soon as it's announced so
 * the next is decoded before the current one ends (PLAN.md §3.2). Measures loudness while playing,
 * which is what moves the avatar's mouth.
 *
 * Web Audio rather than <audio> elements: gapless scheduling, and an AnalyserNode for the level.
 */
export class AudioPlayer {
  /** Called every animation frame while speaking, with loudness 0-1. */
  onLevel?: (level: number) => void;
  onPlayingChange?: (playing: boolean) => void;

  private context: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private samples: Float32Array<ArrayBuffer> | null = null;

  private replyId: string | null = null;
  private nextSeq = 0;
  private readonly decoded = new Map<number, Promise<AudioBuffer | null>>();
  private source: AudioBufferSourceNode | null = null;
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

  enqueue(clip: Clip): void {
    if (clip.replyId !== this.replyId) {
      // A new reply supersedes whatever was still being said.
      this.stop();
      this.replyId = clip.replyId;
      this.nextSeq = 0;
    }

    if (!this.decoded.has(clip.seq)) this.decoded.set(clip.seq, this.load(clip.url));
    void this.pump();
  }

  /** Silence, now: the user interrupted, started talking, or the conversation was cleared. */
  stop(): void {
    this.replyId = null;
    this.decoded.clear();
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
      if (!this.source) this.setPlaying(false);
      return;
    }

    const buffer = await pending;
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
      this.source = null;
      void this.pump();
    };
    this.source = source;
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
        if (this.analyser && this.samples) {
          this.analyser.getFloatTimeDomainData(this.samples);
          let sum = 0;
          for (const s of this.samples) sum += s * s;
          const rms = Math.sqrt(sum / this.samples.length);
          this.onLevel?.(Math.min(1, rms * 5));
        }
        this.frame = requestAnimationFrame(measure);
      };
      this.frame = requestAnimationFrame(measure);
    } else {
      this.onLevel?.(0);
    }
  }
}
