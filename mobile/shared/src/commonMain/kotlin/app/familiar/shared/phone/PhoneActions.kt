package app.familiar.shared.phone

import app.familiar.shared.platform.DevicePhone
import app.familiar.shared.platform.HandOff
import app.familiar.shared.platform.HandedOff
import app.familiar.shared.platform.PhoneContact
import app.familiar.shared.protocol.Capabilities
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

/**
 * The phone's side of texting, calling, directions and "where am I" (BACKLOG item 21): the server's
 * `tool.invoke` in, what the familiar should know out. The person is matched to a contact here, on the
 * phone; what goes back is their name and how it was handed off, never the number.
 */
class PhoneActions(private val phone: DevicePhone) {
    /** What this phone offers the server. */
    val capabilities: Set<String> = setOf(Capabilities.PHONE_TEXT, Capabilities.PHONE_CALL, Capabilities.MAPS_NAVIGATE, Capabilities.LOCATION_READ)

    /** Carries out [tool]; a failure throws with what to tell the user. */
    suspend fun perform(tool: String, arguments: JsonObject): JsonObject {
        fun text(name: String) = arguments[name]?.jsonPrimitive?.contentOrNull?.trim()?.takeIf { it.isNotEmpty() }
        return when (tool) {
            Capabilities.PHONE_TEXT -> {
                val (name, number) = resolve(text("to") ?: fail("Say who to text."))
                val how = phone.handOff(HandOff.Text(name, number, text("message") ?: fail("Say what the text should say.")))
                handedOff(name, how, "Messages opened with the text written; they tap Send.", "Messages with the text written")
            }

            Capabilities.PHONE_CALL -> {
                val (name, number) = resolve(text("to") ?: fail("Say who to call."))
                handedOff(name, phone.handOff(HandOff.Call(name, number)), "The dialler opened with the number in; they tap Call.", "the dialler ready to call")
            }

            Capabilities.MAPS_NAVIGATE -> {
                val destination = text("destination") ?: fail("Ask where they want to go.")
                val how = phone.handOff(HandOff.Navigate(destination, text("mode") ?: "driving"))
                buildJsonObject {
                    put("destination", destination)
                    put("how", describe(how, "Maps opened with directions.", "Maps with directions"))
                }
            }

            Capabilities.LOCATION_READ -> buildJsonObject { put("place", phone.place()) }

            else -> fail("This phone can't do $tool.")
        }
    }

    /** The contact (or number) meant by [to], as the name to show and the number to use. */
    internal suspend fun resolve(to: String): Pair<String, String> {
        if (isNumber(to)) return to to to

        val found = phone.contacts(to)
            ?: fail("Familiar isn't allowed to see the contacts on this phone. Ask them to allow it in the app, or to give the number.")
        val byName = found.groupBy { it.name }
        val exact = byName.filterKeys { it.equals(to, ignoreCase = true) }
        val candidates = exact.ifEmpty { byName }
        return when (candidates.size) {
            0 -> fail("There's no contact called \"$to\" on their phone. Ask who they mean, or for the number.")
            1 -> {
                val (name, numbers) = candidates.entries.single()
                name to (numbers.firstOrNull(PhoneContact::mobile) ?: numbers.first()).number
            }
            else -> fail("More than one contact matches \"$to\": ${candidates.keys.take(5).joinToString(", ")}. Ask which.")
        }
    }

    private fun handedOff(name: String, how: HandedOff, opened: String, inNotification: String) = buildJsonObject {
        put("to", name)
        put("how", describe(how, opened, inNotification))
    }

    private fun describe(how: HandedOff, opened: String, inNotification: String) = when (how) {
        HandedOff.Opened -> opened
        HandedOff.Notified -> "Familiar wasn't on screen, so it's in a notification on their phone: tapping it opens $inNotification."
    }

    private fun fail(message: String): Nothing = throw IllegalStateException(message)

    internal companion object {
        /** A phone number as said or typed: digits, maybe a +, spaces, dashes, brackets. */
        fun isNumber(text: String) = text.count(Char::isDigit) >= 3 && text.all { it.isDigit() || it in "+ -().#*" }
    }
}
