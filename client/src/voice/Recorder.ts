/** Formats in order of preference: Opus where the browser can, AAC on Safari. */
const formats = ['audio/webm;codecs=opus', 'audio/ogg;codecs=opus', 'audio/mp4'];

/** Shorter than this is a mis-tap, not something said. */
const MIN_MS = 350;

/**
 * Push-to-talk recording of one utterance (PLAN.md §3.2). Echo cancellation and noise suppression
 * on, so the familiar's own voice from the speakers isn't transcribed back at it.
 */
export class Recorder {
  static get supported(): boolean {
    return typeof MediaRecorder !== 'undefined' && !!navigator.mediaDevices?.getUserMedia;
  }

  private stream: MediaStream | null = null;
  private recorder: MediaRecorder | null = null;
  private chunks: Blob[] = [];
  private started = 0;

  get recording(): boolean {
    return this.recorder?.state === 'recording';
  }

  /** Asks for the microphone (the first time) and starts recording. */
  async start(): Promise<void> {
    this.stream = await navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 },
    });
    const mimeType = formats.find((f) => MediaRecorder.isTypeSupported(f));
    this.recorder = new MediaRecorder(this.stream, mimeType ? { mimeType } : undefined);
    this.chunks = [];
    this.recorder.ondataavailable = (event) => {
      if (event.data.size > 0) this.chunks.push(event.data);
    };
    this.recorder.start();
    this.started = performance.now();
  }

  /** Stops and returns the utterance, or null if it was too short to be anything. */
  stop(): Promise<Blob | null> {
    const recorder = this.recorder;
    if (!recorder || recorder.state === 'inactive') return Promise.resolve(null);

    const duration = performance.now() - this.started;
    return new Promise((resolve) => {
      recorder.onstop = () => {
        const blob = new Blob(this.chunks, { type: recorder.mimeType || 'audio/webm' });
        this.release();
        resolve(duration < MIN_MS || blob.size === 0 ? null : blob);
      };
      recorder.stop();
    });
  }

  /** Throws the recording away. */
  cancel(): void {
    if (this.recorder && this.recorder.state !== 'inactive') {
      this.recorder.onstop = null;
      this.recorder.stop();
    }
    this.release();
  }

  private release() {
    this.stream?.getTracks().forEach((t) => t.stop());
    this.stream = null;
    this.recorder = null;
  }
}
