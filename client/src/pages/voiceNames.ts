// Kokoro voice ids are `<language><gender>_<name>`: "af_heart" is American English, female, "Heart".
const languages: Record<string, string> = {
  a: 'American English',
  b: 'British English',
  e: 'Spanish',
  f: 'French',
  h: 'Hindi',
  i: 'Italian',
  j: 'Japanese',
  p: 'Portuguese',
  z: 'Mandarin',
};

const genders: Record<string, string> = { f: 'female', m: 'male' };

export interface VoiceOption {
  id: string;
  label: string;
  language: string;
}

export function describeVoice(id: string): VoiceOption {
  const match = /^([a-z])([fm])_(.+)$/.exec(id);
  if (!match) return { id, label: id, language: 'Other' };
  const [, lang, gender, name] = match;
  const pretty = name!.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
  return {
    id,
    label: `${pretty} (${genders[gender!] ?? gender})`,
    language: languages[lang!] ?? 'Other',
  };
}

/** Voices grouped by language, English first. */
export function groupVoices(ids: string[]): [string, VoiceOption[]][] {
  const groups = new Map<string, VoiceOption[]>();
  for (const voice of ids.map(describeVoice)) {
    groups.set(voice.language, [...(groups.get(voice.language) ?? []), voice]);
  }
  const order = (language: string) => (language.includes('English') ? 0 : 1);
  return [...groups.entries()].sort(([a], [b]) => order(a) - order(b) || a.localeCompare(b));
}
