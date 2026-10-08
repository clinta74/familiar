package app.familiar.shared.phone

import app.familiar.shared.platform.ContactAddress
import app.familiar.shared.platform.DevicePhone
import app.familiar.shared.platform.HandOff
import app.familiar.shared.platform.HandedOff
import app.familiar.shared.platform.PhoneContact
import app.familiar.shared.protocol.Capabilities
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.add
import kotlinx.serialization.json.addJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray

/**
 * The phone's side of texting, calling, directions and "where am I" (BACKLOG item 21): the server's
 * `tool.invoke` in, what the familiar should know out. The person is matched to a contact here, on the
 * phone; what goes back is their name and how it was handed off, never the number.
 */
class PhoneActions(private val phone: DevicePhone) {
    /** What this phone offers the server. */
    val capabilities: Set<String> =
        setOf(Capabilities.PHONE_TEXT, Capabilities.PHONE_CALL, Capabilities.MAPS_NAVIGATE, Capabilities.LOCATION_READ, Capabilities.CONTACTS_READ)

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
                val mode = text("mode") ?: "driving"
                val contact = text("to_contact")
                if (contact != null) {
                    // To someone's: their address found here, and not said - the familiar learns only whose.
                    val (name, address) = addressOf(contact, text("which"))
                    val how = phone.handOff(HandOff.Navigate(address.address, mode, shownAs = "$name's"))
                    buildJsonObject {
                        put("to", name)
                        put("how", describe(how, "Maps opened with directions to $name's.", "Maps with directions to $name's"))
                    }
                } else {
                    val destination = text("destination") ?: fail("Ask where they want to go.")
                    val how = phone.handOff(HandOff.Navigate(destination, mode))
                    buildJsonObject {
                        put("destination", destination)
                        put("how", describe(how, "Maps opened with directions.", "Maps with directions"))
                    }
                }
            }

            Capabilities.CONTACTS_READ -> details(text("name") ?: fail("Ask who they mean."), text("want") ?: "phone", text("which"))

            Capabilities.LOCATION_READ -> buildJsonObject { put("place", phone.place()) }

            else -> fail("This phone can't do $tool.")
        }
    }

    /** The contact (or number) meant by [to], as the name to show and the number to use. */
    internal suspend fun resolve(to: String): Pair<String, String> {
        if (isNumber(to)) return to to to

        val found = phone.contacts(to) ?: fail(NOT_ALLOWED + " Or ask for the number.")
        val (name, numbers) = choose(to, found, PhoneContact::name, "Ask who they mean, or for the number.")
        return name to (numbers.firstOrNull(PhoneContact::mobile) ?: numbers.first()).number
    }

    /** The address of the contact meant by [to]: the one asked for ([which]), or their only one. */
    private suspend fun addressOf(to: String, which: String?): Pair<String, ContactAddress> {
        val found = phone.addresses(to) ?: fail(NOT_ALLOWED)
        if (found.isEmpty()) fail("There's no address for \"$to\" in their contacts. Ask where to go instead.")
        val (name, addresses) = choose(to, found, ContactAddress::name, "Ask who they mean.")
        val options = labelled(addresses, which, ContactAddress::label)
        return name to when {
            options.size == 1 -> options.single()
            options.isEmpty() -> fail("$name has no $which address in their contacts: there's ${labels(addresses, ContactAddress::label)}. Ask which.")
            else -> fail("$name has more than one address: ${labels(addresses, ContactAddress::label)}. Ask which.")
        }
    }

    /**
     * One contact's number or address, to say: the only contact detail that leaves the phone, and
     * only when asked for.
     */
    private suspend fun details(to: String, want: String, which: String?): JsonObject {
        val numbers = if (want == "address") emptyList() else phone.contacts(to) ?: fail(NOT_ALLOWED)
        val addresses = if (want == "phone") emptyList() else phone.addresses(to) ?: fail(NOT_ALLOWED)
        val names = numbers.map(PhoneContact::name) + addresses.map(ContactAddress::name)
        val (name, _) = choose(to, names, { it }, "Ask who they mean.")
        val theirNumbers = labelled(numbers.filter { it.name == name }, which, PhoneContact::label)
        val theirAddresses = labelled(addresses.filter { it.name == name }, which, ContactAddress::label)
        if (theirNumbers.isEmpty() && theirAddresses.isEmpty()) {
            fail("$name has no ${listOfNotNull(which, if (want == "both") "number or address" else want).joinToString(" ")} in their contacts.")
        }
        return buildJsonObject {
            put("name", name)
            if (theirNumbers.isNotEmpty()) {
                putJsonArray("numbers") {
                    theirNumbers.forEach { addJsonObject { put("number", it.number); put("kind", it.label) } }
                }
            }
            if (theirAddresses.isNotEmpty()) {
                putJsonArray("addresses") {
                    theirAddresses.forEach { addJsonObject { put("address", it.address); put("kind", it.label) } }
                }
            }
            put("note", "From their phone's contacts, to tell them because they asked. Don't remember it.")
        }
    }

    /**
     * Which contact [to] means, among [found] (several entries each, perhaps): an exact name wins over
     * names that start the same; two people who both match are asked about.
     */
    private fun <T> choose(to: String, found: List<T>, nameOf: (T) -> String, ifNone: String): Pair<String, List<T>> {
        val byName = found.groupBy(nameOf)
        val exact = byName.filterKeys { it.equals(to, ignoreCase = true) }
        val candidates = exact.ifEmpty { byName }
        return when (candidates.size) {
            0 -> fail("There's no contact called \"$to\" on their phone. $ifNone")
            1 -> candidates.entries.single().toPair()
            else -> fail("More than one contact matches \"$to\": ${candidates.keys.take(5).joinToString(", ")}. Ask which.")
        }
    }

    /** The ones [which] asks for ("work" matches "Work" and "Work mobile"), or all of them. */
    private fun <T> labelled(items: List<T>, which: String?, labelOf: (T) -> String): List<T> =
        if (which == null) items else items.filter { labelOf(it).contains(which, ignoreCase = true) }

    private fun <T> labels(items: List<T>, labelOf: (T) -> String) =
        items.map { labelOf(it).ifBlank { "one with no label" } }.distinct().joinToString(", ")

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

        const val NOT_ALLOWED = "Familiar isn't allowed to see the contacts on this phone. Ask them to allow it in the app."
    }
}
