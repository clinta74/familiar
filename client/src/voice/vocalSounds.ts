// A PROTOTYPE (BACKLOG item 14): the familiar's vocal sounds - a giggle, a gasp, a sigh - made in Web
// Audio, since Kokoro reads "hehehe" out as a word. Not a recording of a person: a buzzing source
// shaped into vowels by formant filters, with breath noise and a pitch contour per sound, pitched to
// the familiar's voice. Cartoonish on purpose, like a plush toy's.

export type VocalSound = 'giggle' | 'gasp' | 'sigh' | 'yawn' | 'hmm' | 'ooh';

export const vocalSounds: readonly VocalSound[] = ['giggle', 'gasp', 'sigh', 'yawn', 'hmm', 'ooh'];

/** Formants (F1-F3, Hz) of the vowels the sounds glide through. */
const vowels = {
  ah: [730, 1090, 2440],
  eh: [530, 1840, 2480],
  ih: [390, 1990, 2550],
  oh: [570, 840, 2410],
  oo: [300, 870, 2240],
  uh: [520, 1190, 2390],
} as const;
type Vowel = keyof typeof vowels;

/** How loud, under the voice. */
const VOLUME = 0.5;

/**
 * The speaking pitch (Hz) for a Kokoro voice id ("af_heart": American, female): higher for female
 * voices, lower for male. A plush toy sits a little above a person.
 */
export function voicePitch(voice: string | null | undefined): number {
  const male = /^[a-z]m_/.test(voice ?? '');
  return male ? 135 : 235;
}

interface Voice {
  context: BaseAudioContext;
  out: AudioNode;
  /** The pitch, followed over time. */
  pitch: AudioParam;
  /** The voiced part's loudness. */
  voiced: AudioParam;
  /** The breath's loudness. */
  breath: AudioParam;
  /** The formants' frequencies, three per vowel. */
  formants: AudioParam[];
  stop: (at: number) => void;
}

/** A source (buzz plus breath) through three formant filters: a mouth that can be steered. */
function voice(context: BaseAudioContext, out: AudioNode, at: number, pitch: number, vowel: Vowel): Voice {
  const buzz = context.createOscillator();
  buzz.type = 'sawtooth';
  buzz.frequency.setValueAtTime(pitch, at);

  // A little vibrato, so it doesn't sound like a machine.
  const wobble = context.createOscillator();
  const wobbleDepth = context.createGain();
  wobble.frequency.value = 5.5;
  wobbleDepth.gain.value = pitch * 0.012;
  wobble.connect(wobbleDepth).connect(buzz.frequency);

  const voiced = context.createGain();
  voiced.gain.setValueAtTime(0, at);
  buzz.connect(voiced);

  const noiseBuffer = context.createBuffer(1, context.sampleRate * 2, context.sampleRate);
  const data = noiseBuffer.getChannelData(0);
  for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
  const noise = context.createBufferSource();
  noise.buffer = noiseBuffer;
  noise.loop = true;
  const breath = context.createGain();
  breath.gain.setValueAtTime(0, at);
  noise.connect(breath);

  const mix = context.createGain();
  mix.gain.value = 1;
  const formants: AudioParam[] = [];
  vowels[vowel].forEach((frequency, i) => {
    const filter = context.createBiquadFilter();
    filter.type = 'bandpass';
    filter.frequency.setValueAtTime(frequency, at);
    filter.Q.value = 8 + i * 4;
    const level = context.createGain();
    level.gain.value = [1, 0.6, 0.25][i]!;
    voiced.connect(filter);
    breath.connect(filter);
    filter.connect(level).connect(mix);
    formants.push(filter.frequency);
  });
  // The formant filters are narrow; bring the level back up.
  const makeUp = context.createGain();
  makeUp.gain.value = 4;
  mix.connect(makeUp).connect(out);

  buzz.start(at);
  wobble.start(at);
  noise.start(at);
  return {
    context,
    out,
    pitch: buzz.frequency,
    voiced: voiced.gain,
    breath: breath.gain,
    formants,
    stop: (end) => {
      buzz.stop(end);
      wobble.stop(end);
      noise.stop(end);
    },
  };
}

/** Steers the mouth to [vowel] by [at], over [glide] seconds. */
function shape(v: Voice, vowel: Vowel, at: number, glide = 0.08) {
  vowels[vowel].forEach((frequency, i) => {
    v.formants[i]!.setTargetAtTime(frequency, at, glide / 3);
  });
}

type Make = (context: BaseAudioContext, out: AudioNode, at: number, pitch: number) => void;

const makers: Record<VocalSound, Make> = {
  // "Heh-heh-heh-heh": short breathy blips, high and quick, each falling a little, the run rising.
  giggle(context, out, at, pitch) {
    const blips = 5;
    for (let i = 0; i < blips; i++) {
      const start = at + i * 0.105;
      const top = pitch * (1.45 + i * 0.05);
      const v = voice(context, out, start, top, 'eh');
      v.pitch.setValueAtTime(top, start);
      v.pitch.exponentialRampToValueAtTime(top * 0.82, start + 0.08);
      v.breath.setValueAtTime(0.0001, start);
      v.breath.linearRampToValueAtTime(0.35, start + 0.012);
      v.breath.linearRampToValueAtTime(0.05, start + 0.03);
      v.voiced.setValueAtTime(0.0001, start);
      v.voiced.linearRampToValueAtTime(0.9 - i * 0.1, start + 0.025);
      v.voiced.exponentialRampToValueAtTime(0.0001, start + 0.085);
      v.breath.exponentialRampToValueAtTime(0.0001, start + 0.09);
      v.stop(start + 0.1);
    }
  },

  // A quick in-breath with a squeak of voice at the top: "hah!"
  gasp(context, out, at, pitch) {
    const v = voice(context, out, at, pitch * 1.6, 'ah');
    v.breath.setValueAtTime(0.0001, at);
    v.breath.exponentialRampToValueAtTime(0.9, at + 0.12);
    v.breath.exponentialRampToValueAtTime(0.0001, at + 0.3);
    v.voiced.setValueAtTime(0.0001, at + 0.06);
    v.voiced.linearRampToValueAtTime(0.35, at + 0.12);
    v.voiced.exponentialRampToValueAtTime(0.0001, at + 0.24);
    v.pitch.setValueAtTime(pitch * 1.4, at);
    v.pitch.exponentialRampToValueAtTime(pitch * 2, at + 0.22);
    shape(v, 'oh', at + 0.1, 0.12);
    v.stop(at + 0.32);
  },

  // A long, falling, breathy "haaah".
  sigh(context, out, at, pitch) {
    const length = 1.1;
    const v = voice(context, out, at, pitch * 1.15, 'ah');
    v.breath.setValueAtTime(0.0001, at);
    v.breath.linearRampToValueAtTime(0.6, at + 0.1);
    v.breath.linearRampToValueAtTime(0.3, at + 0.4);
    v.breath.exponentialRampToValueAtTime(0.0001, at + length);
    v.voiced.setValueAtTime(0.0001, at + 0.05);
    v.voiced.linearRampToValueAtTime(0.45, at + 0.2);
    v.voiced.exponentialRampToValueAtTime(0.0001, at + length * 0.9);
    v.pitch.setValueAtTime(pitch * 1.15, at);
    v.pitch.exponentialRampToValueAtTime(pitch * 0.72, at + length);
    shape(v, 'uh', at + 0.5, 0.4);
    v.stop(at + length + 0.05);
  },

  // A wide "aaaah-ohm": rising then falling, the mouth closing at the end.
  yawn(context, out, at, pitch) {
    const length = 1.6;
    const v = voice(context, out, at, pitch * 0.95, 'ah');
    v.breath.setValueAtTime(0.0001, at);
    v.breath.linearRampToValueAtTime(0.35, at + 0.2);
    v.breath.exponentialRampToValueAtTime(0.0001, at + length);
    v.voiced.setValueAtTime(0.0001, at);
    v.voiced.linearRampToValueAtTime(0.7, at + 0.3);
    v.voiced.linearRampToValueAtTime(0.5, at + 1.1);
    v.voiced.exponentialRampToValueAtTime(0.0001, at + length);
    v.pitch.setValueAtTime(pitch * 0.95, at);
    v.pitch.linearRampToValueAtTime(pitch * 1.35, at + 0.45);
    v.pitch.exponentialRampToValueAtTime(pitch * 0.7, at + length);
    shape(v, 'oh', at + 0.9, 0.3);
    shape(v, 'oo', at + 1.3, 0.25);
    v.stop(at + length + 0.05);
  },

  // A thoughtful closed-mouth hum: "hmm?", a dip and a lift.
  hmm(context, out, at, pitch) {
    const length = 0.75;
    const v = voice(context, out, at, pitch, 'oo');
    // A hum is nasal: everything above the first formant mostly gone.
    v.formants[1]!.setValueAtTime(1000, at);
    v.formants[2]!.setValueAtTime(2500, at);
    v.voiced.setValueAtTime(0.0001, at);
    v.voiced.linearRampToValueAtTime(0.8, at + 0.06);
    v.voiced.setValueAtTime(0.8, at + length - 0.15);
    v.voiced.exponentialRampToValueAtTime(0.0001, at + length);
    v.pitch.setValueAtTime(pitch * 1.05, at);
    v.pitch.linearRampToValueAtTime(pitch * 0.92, at + 0.3);
    v.pitch.linearRampToValueAtTime(pitch * 1.18, at + length);
    v.stop(at + length + 0.05);
  },

  // An impressed "ooooh": up, then down.
  ooh(context, out, at, pitch) {
    const length = 0.85;
    const v = voice(context, out, at, pitch * 1.1, 'oo');
    v.breath.setValueAtTime(0.0001, at);
    v.breath.linearRampToValueAtTime(0.15, at + 0.05);
    v.breath.exponentialRampToValueAtTime(0.0001, at + 0.3);
    v.voiced.setValueAtTime(0.0001, at);
    v.voiced.linearRampToValueAtTime(0.85, at + 0.08);
    v.voiced.exponentialRampToValueAtTime(0.0001, at + length);
    v.pitch.setValueAtTime(pitch * 1.1, at);
    v.pitch.linearRampToValueAtTime(pitch * 1.55, at + 0.3);
    v.pitch.exponentialRampToValueAtTime(pitch * 1.0, at + length);
    v.stop(at + length + 0.05);
  },
};

/** Plays [sound] in a voice pitched for [voice] (a Kokoro voice id). */
export function playVocalSound(context: BaseAudioContext, sound: VocalSound, voiceId: string | null | undefined, destination: AudioNode = context.destination): void {
  // A limiter after the level: a high giggle's resonances can peak well over a low hum's.
  const level = context.createGain();
  const limiter = context.createDynamicsCompressor();
  level.gain.value = VOLUME;
  limiter.threshold.value = -12;
  limiter.knee.value = 6;
  limiter.ratio.value = 12;
  limiter.attack.value = 0.002;
  limiter.release.value = 0.1;
  level.connect(limiter).connect(destination);
  makers[sound](context, level, context.currentTime + 0.01, voicePitch(voiceId));
}

/** How long each lasts, for rendering it offline. */
export const vocalLength: Record<VocalSound, number> = { giggle: 0.65, gasp: 0.4, sigh: 1.25, yawn: 1.75, hmm: 0.85, ooh: 0.95 };
