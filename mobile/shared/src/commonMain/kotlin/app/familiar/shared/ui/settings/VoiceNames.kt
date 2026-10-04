package app.familiar.shared.ui.settings

// Kokoro voice ids are `<language><gender>_<name>`: "af_heart" is American English, female, "Heart".
// The web client's voiceNames.ts, ported.

private val languages = mapOf(
    'a' to "American English",
    'b' to "British English",
    'e' to "Spanish",
    'f' to "French",
    'h' to "Hindi",
    'i' to "Italian",
    'j' to "Japanese",
    'p' to "Portuguese",
    'z' to "Mandarin",
)

private val genders = mapOf('f' to "female", 'm' to "male")

private val voiceId = Regex("^([a-z])([fm])_(.+)$")

data class VoiceOption(val id: String, val label: String, val language: String)

fun describeVoice(id: String): VoiceOption {
    val match = voiceId.matchEntire(id) ?: return VoiceOption(id, id, "Other")
    val (lang, gender, name) = match.destructured
    val pretty = name.split('_').joinToString(" ") { word -> word.replaceFirstChar { it.uppercaseChar() } }
    return VoiceOption(id, "$pretty (${genders[gender[0]] ?: gender})", languages[lang[0]] ?: "Other")
}

/** Voices grouped by language, English first, then alphabetically. */
fun groupVoices(ids: List<String>): List<Pair<String, List<VoiceOption>>> =
    ids.map(::describeVoice)
        .groupBy { it.language }
        .toList()
        .sortedWith(compareBy<Pair<String, List<VoiceOption>>> { if ("English" in it.first) 0 else 1 }.thenBy { it.first })
