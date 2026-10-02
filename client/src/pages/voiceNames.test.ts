import { describe, expect, it } from 'vitest';
import { describeVoice, groupVoices } from './voiceNames';

describe('voice names', () => {
  it('reads Kokoro ids', () => {
    expect(describeVoice('af_heart')).toEqual({ id: 'af_heart', label: 'Heart (female)', language: 'American English' });
    expect(describeVoice('bm_george')).toMatchObject({ label: 'George (male)', language: 'British English' });
  });

  it('passes anything else through', () => {
    expect(describeVoice('custom-voice')).toEqual({ id: 'custom-voice', label: 'custom-voice', language: 'Other' });
  });

  it('groups by language, English first', () => {
    expect(groupVoices(['ff_siwis', 'bf_emma', 'af_heart']).map(([language]) => language))
      .toEqual(['American English', 'British English', 'French']);
  });
});
