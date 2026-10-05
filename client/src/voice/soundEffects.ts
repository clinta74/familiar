import type { SoundEffect } from '../protocol/generated';

// The familiar's sound effects (the server's SoundEffect): synthesised with Web Audio, so there's no
// file to fetch or licence to track, and they're the same on the web and in the phone apps (whose
// avatar page plays them). Not its voice: vocal sounds (a giggle, a gasp) will be made in that.

/** How loud, under the voice: a garnish, not an alarm. */
const VOLUME = 0.22;

type Synth = (context: BaseAudioContext, out: AudioNode, at: number) => void;

/** A tone with an attack and an exponential fade; returns the oscillator to shape further. */
function tone(context: BaseAudioContext, out: AudioNode, type: OscillatorType, frequency: number, at: number, length: number, level = 1) {
  const oscillator = context.createOscillator();
  const gain = context.createGain();
  oscillator.type = type;
  oscillator.frequency.setValueAtTime(frequency, at);
  gain.gain.setValueAtTime(0, at);
  gain.gain.linearRampToValueAtTime(level, at + 0.008);
  gain.gain.exponentialRampToValueAtTime(0.0001, at + length);
  oscillator.connect(gain).connect(out);
  oscillator.start(at);
  oscillator.stop(at + length + 0.02);
  return oscillator;
}

/** White noise, for air. */
function noise(context: BaseAudioContext, length: number) {
  const buffer = context.createBuffer(1, Math.ceil(context.sampleRate * length), context.sampleRate);
  const data = buffer.getChannelData(0);
  for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
  const source = context.createBufferSource();
  source.buffer = buffer;
  return source;
}

/** A brassy note: a sawtooth through a lowpass that opens as it sounds. */
function brass(context: BaseAudioContext, out: AudioNode, frequency: number, at: number, length: number, level = 0.5) {
  const filter = context.createBiquadFilter();
  filter.type = 'lowpass';
  filter.frequency.setValueAtTime(600, at);
  filter.frequency.linearRampToValueAtTime(2400, at + 0.05);
  filter.frequency.exponentialRampToValueAtTime(900, at + length);
  filter.connect(out);
  return tone(context, filter, 'sawtooth', frequency, at, length, level);
}

const synths: Record<SoundEffect, Synth> = {
  // A bubble: a sine dropping fast, with a click of noise on top.
  pop(context, out, at) {
    tone(context, out, 'sine', 900, at, 0.12).frequency.exponentialRampToValueAtTime(180, at + 0.1);
    const click = noise(context, 0.02);
    const gain = context.createGain();
    gain.gain.setValueAtTime(0.25, at);
    gain.gain.exponentialRampToValueAtTime(0.001, at + 0.02);
    click.connect(gain).connect(out);
    click.start(at);
  },

  // A spring: a low triangle whose pitch wobbles, the wobble dying away.
  boing(context, out, at) {
    const length = 0.75;
    const spring = tone(context, out, 'triangle', 190, at, length, 0.9);
    const wobble = context.createOscillator();
    const depth = context.createGain();
    wobble.frequency.setValueAtTime(14, at);
    wobble.frequency.linearRampToValueAtTime(9, at + length);
    depth.gain.setValueAtTime(130, at);
    depth.gain.exponentialRampToValueAtTime(4, at + length);
    wobble.connect(depth).connect(spring.frequency);
    spring.frequency.linearRampToValueAtTime(280, at + length);
    wobble.start(at);
    wobble.stop(at + length);
  },

  // Bright notes from a pentatonic scale, falling over each other.
  sparkle(context, out, at) {
    const notes = [2093, 2349, 2637, 3136, 3520, 4186];
    for (let i = 0; i < 7; i++) {
      const note = notes[Math.floor(Math.random() * notes.length)]!;
      tone(context, out, 'sine', note, at + i * 0.055 + Math.random() * 0.02, 0.35, 0.45);
    }
  },

  // A fanfare: a short chord, then a longer one a fourth higher.
  tada(context, out, at) {
    const chord = (root: number, start: number, length: number) =>
      [1, 1.26, 1.5].forEach((ratio) => brass(context, out, root * ratio, start, length, 0.32));
    chord(392, at, 0.13);
    chord(523.25, at + 0.16, 0.75);
  },

  // Air rushing past: noise through a bandpass sweeping up, swelling and fading.
  whoosh(context, out, at) {
    const length = 0.55;
    const air = noise(context, length);
    const band = context.createBiquadFilter();
    const gain = context.createGain();
    band.type = 'bandpass';
    band.Q.value = 1.2;
    band.frequency.setValueAtTime(300, at);
    band.frequency.exponentialRampToValueAtTime(3200, at + length);
    gain.gain.setValueAtTime(0.0001, at);
    gain.gain.exponentialRampToValueAtTime(0.9, at + length * 0.45);
    gain.gain.exponentialRampToValueAtTime(0.0001, at + length);
    air.connect(band).connect(gain).connect(out);
    air.start(at);
  },

  // The sad trombone: three falling notes and a long, wobbling last one.
  womp(context, out, at) {
    const notes = [293.66, 277.18, 261.63];
    notes.forEach((note, i) => brass(context, out, note, at + i * 0.32, 0.3, 0.4));
    const last = brass(context, out, 246.94, at + 0.96, 0.9, 0.4);
    const wobble = context.createOscillator();
    const depth = context.createGain();
    wobble.frequency.value = 6;
    depth.gain.value = 5;
    wobble.connect(depth).connect(last.frequency);
    wobble.start(at + 1.1);
    wobble.stop(at + 1.9);
  },

  // "Ba-dum-tss": two toms, high then low, and a ringing cymbal. For a joke (the womp's partner).
  rimshot(context, out, at) {
    drum(context, out, 210, at, 0.18);
    drum(context, out, 140, at + 0.17, 0.22);
    cymbal(context, out, at + 0.42, 0.9, 0.55);
  },

  // A snare roll that builds, and a hit to finish: before a reveal.
  drumroll(context, out, at) {
    const length = 1.3;
    const strokes = Math.round(length * 28);
    for (let i = 0; i < strokes; i++) {
      snare(context, out, at + i / 28 + (Math.random() - 0.5) * 0.006, 0.12 + 0.5 * (i / strokes));
    }
    drum(context, out, 120, at + length + 0.02, 0.3);
    cymbal(context, out, at + length + 0.02, 0.8, 0.6);
  },

  // A bright bell: a good idea, a right answer. Inharmonic partials, as a real bell has.
  ding(context, out, at) {
    [[1, 1, 1.4], [2.76, 0.4, 0.9], [5.4, 0.2, 0.5], [8.93, 0.08, 0.3]].forEach(([ratio, level, length]) =>
      tone(context, out, 'sine', 1046.5 * ratio!, at, length!, 0.55 * level!));
  },

  // A slide whistle going up: up we go.
  slide(context, out, at) {
    const length = 0.6;
    const whistle = tone(context, out, 'sine', 500, at, length, 0.55);
    whistle.frequency.exponentialRampToValueAtTime(1700, at + length * 0.9);
    const wobble = context.createOscillator();
    const depth = context.createGain();
    wobble.frequency.value = 7;
    depth.gain.value = 18;
    wobble.connect(depth).connect(whistle.frequency);
    wobble.start(at);
    wobble.stop(at + length);
  },

  // Two soft falling notes, "bloop-bloop": a little oops, gentler than the trombone.
  uhoh(context, out, at) {
    [[659.25, 0], [523.25, 0.2]].forEach(([note, delay]) => {
      const blip = tone(context, out, 'triangle', note!, at + delay!, 0.26, 0.7);
      blip.frequency.exponentialRampToValueAtTime(note! * 0.94, at + delay! + 0.22);
    });
  },
};


/** A drum: a sine thump whose pitch drops, with a tap of noise for the stick. */
function drum(context: BaseAudioContext, out: AudioNode, pitch: number, at: number, length: number) {
  tone(context, out, 'sine', pitch * 1.6, at, length, 1).frequency.exponentialRampToValueAtTime(pitch, at + 0.05);
  const stick = noise(context, 0.03);
  const gain = context.createGain();
  gain.gain.setValueAtTime(0.3, at);
  gain.gain.exponentialRampToValueAtTime(0.001, at + 0.03);
  stick.connect(gain).connect(out);
  stick.start(at);
}

/** A snare stroke: a short burst of noise through a mid bandpass. */
function snare(context: BaseAudioContext, out: AudioNode, at: number, level: number) {
  const burst = noise(context, 0.08);
  const band = context.createBiquadFilter();
  const gain = context.createGain();
  band.type = 'bandpass';
  band.frequency.value = 1900;
  band.Q.value = 0.8;
  gain.gain.setValueAtTime(level, at);
  gain.gain.exponentialRampToValueAtTime(0.001, at + 0.07);
  burst.connect(band).connect(gain).connect(out);
  burst.start(at);
}

/** A cymbal: high, bright noise that rings and fades. */
function cymbal(context: BaseAudioContext, out: AudioNode, at: number, length: number, level: number) {
  const shimmer = noise(context, length);
  const high = context.createBiquadFilter();
  const gain = context.createGain();
  high.type = 'highpass';
  high.frequency.value = 6500;
  gain.gain.setValueAtTime(level, at);
  gain.gain.exponentialRampToValueAtTime(0.001, at + length);
  shimmer.connect(high).connect(gain).connect(out);
  shimmer.start(at);
}

/** The effects there are, in a sensible order for trying them out. */
export const soundEffects = Object.keys(synths) as SoundEffect[];

/** Plays [sound] now, quietly, into [destination] (the speakers, not the voice's analyser). */
export function playSoundEffect(context: BaseAudioContext, sound: SoundEffect, destination: AudioNode = context.destination): void {
  const synth = synths[sound];
  if (!synth) return; // From a newer server: a sound this page doesn't know.
  const level = context.createGain();
  level.gain.value = VOLUME;
  level.connect(destination);
  synth(context, level, context.currentTime + 0.01);
}
