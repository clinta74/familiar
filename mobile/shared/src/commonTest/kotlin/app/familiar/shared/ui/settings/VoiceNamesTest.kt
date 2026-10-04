package app.familiar.shared.ui.settings

import kotlin.test.Test
import kotlin.test.assertEquals

class VoiceNamesTest {
    @Test
    fun a_voice_id_reads_as_its_name_gender_and_language() {
        assertEquals(VoiceOption("af_heart", "Heart (female)", "American English"), describeVoice("af_heart"))
        assertEquals(VoiceOption("bm_old_george", "Old George (male)", "British English"), describeVoice("bm_old_george"))
        assertEquals(VoiceOption("custom", "custom", "Other"), describeVoice("custom"))
    }

    @Test
    fun english_comes_first_then_the_rest_alphabetically() {
        val groups = groupVoices(listOf("ff_siwis", "bf_emma", "af_heart", "jf_alpha", "custom"))
        assertEquals(listOf("American English", "British English", "French", "Japanese", "Other"), groups.map { it.first })
    }
}
