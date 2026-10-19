package app.familiar.shared

import app.familiar.shared.phone.PhoneActions
import app.familiar.shared.platform.ContactAddress
import app.familiar.shared.platform.DevicePhone
import app.familiar.shared.platform.HandOff
import app.familiar.shared.platform.HandedOff
import app.familiar.shared.platform.PhoneContact
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonPrimitive
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class PhoneActionsTests {
    private class FakePhone(
        val book: List<PhoneContact>? = listOf(
            PhoneContact("Sam Kim", "+44 7700 900001", mobile = false),
            PhoneContact("Sam Kim", "+44 7700 900002", mobile = true),
            PhoneContact("Mum", "+44 7700 900003", mobile = true),
            PhoneContact("Mummy's Friend Jo", "+44 7700 900004", mobile = true),
            PhoneContact("Sam Patel", "+44 7700 900005", mobile = true),
        ),
        val inFront: Boolean = true,
        val places: List<ContactAddress>? = listOf(
            ContactAddress("Mum", "1 Oak Lane, Leeds", "home"),
            ContactAddress("Sam Kim", "2 Elm Road, Leeds", "home"),
            ContactAddress("Sam Kim", "3 Office Park, Leeds", "work"),
        ),
    ) : DevicePhone {
        val handedOff = mutableListOf<HandOff>()

        private fun matches(contact: String, name: String) = contact.split(' ').any { it.startsWith(name, ignoreCase = true) } || contact.equals(name, ignoreCase = true)

        override suspend fun contacts(name: String) = book?.filter { matches(it.name, name) }

        override suspend fun addresses(name: String) = places?.filter { matches(it.name, name) }

        override fun handOff(handOff: HandOff): HandedOff {
            handedOff += handOff
            return if (inFront) HandedOff.Opened else HandedOff.Notified
        }

        override suspend fun place() = "Headingley, Leeds, England"

        override suspend fun position() = 53.82 to -1.58

        var sends = false
        val sent = mutableListOf<Pair<String, String>>()

        override fun sendsTexts() = sends

        override suspend fun sendText(number: String, message: String) {
            sent += number to message
        }
    }

    private fun args(vararg pairs: Pair<String, String>) = JsonObject(pairs.associate { it.first to JsonPrimitive(it.second) })

    @Test
    fun a_text_goes_to_the_contacts_mobile_and_only_the_name_comes_back() = runTest {
        val phone = FakePhone(book = listOf(PhoneContact("Sam Kim", "+44 7700 900001", false), PhoneContact("Sam Kim", "+44 7700 900002", true)))
        val result = PhoneActions(phone).perform("phone.text", args("to" to "Sam", "message" to "Running late"))

        assertEquals(HandOff.Text("Sam Kim", "+44 7700 900002", "Running late"), phone.handedOff.single())
        assertEquals("Sam Kim", result["to"]!!.jsonPrimitive.content)
        assertFalse(result.toString().contains("7700"), "The number stays on the phone.")
    }

    @Test
    fun sending_texts_itself_is_offered_only_when_switched_on() {
        val phone = FakePhone()
        assertFalse("phone.text.send" in PhoneActions(phone).capabilities)
        phone.sends = true
        assertTrue("phone.text.send" in PhoneActions(phone).capabilities)
    }

    @Test
    fun a_text_to_send_is_checked_first_then_sent_to_the_contact_read_back() = runTest {
        // BACKLOG item 39: the familiar reads back who and what, and sends on a yes.
        val phone = FakePhone(book = listOf(PhoneContact("Mum", "+44 7700 900003", true))).apply { sends = true }
        val actions = PhoneActions(phone)

        val check = actions.perform("phone.text.send", JsonObject(args("to" to "mum", "message" to "Running late") + ("check" to JsonPrimitive(true))))
        assertEquals("Mum", check["to"]!!.jsonPrimitive.content)
        assertEquals("mobile", check["label"]!!.jsonPrimitive.content)
        assertTrue(phone.sent.isEmpty(), "Checking sends nothing.")
        assertFalse(check.toString().contains("7700"), "The number stays on the phone.")

        val result = actions.perform("phone.text.send", args("to" to "mum", "message" to "Running late", "expect" to "Mum"))
        assertEquals(listOf("+44 7700 900003" to "Running late"), phone.sent)
        assertTrue(result["sent"]!!.jsonPrimitive.content.toBoolean())
        assertTrue(phone.handedOff.isEmpty(), "Messages isn't opened.")
    }

    @Test
    fun a_text_isnt_sent_to_someone_other_than_who_was_read_back_or_once_switched_off() = runTest {
        val phone = FakePhone().apply { sends = true }
        val changed = assertFailsWith<IllegalStateException> {
            PhoneActions(phone).perform("phone.text.send", args("to" to "mum", "message" to "hi", "expect" to "Mummy's Friend Jo"))
        }
        assertContains(changed.message!!, "wasn't sent")

        phone.sends = false
        val off = assertFailsWith<IllegalStateException> { PhoneActions(phone).perform("phone.text.send", args("to" to "mum", "message" to "hi")) }
        assertContains(off.message!!, "wasn't sent")
        assertTrue(phone.sent.isEmpty())
    }

    @Test
    fun an_exact_name_wins_over_names_that_start_the_same() = runTest {
        val phone = FakePhone()
        PhoneActions(phone).perform("phone.call", args("to" to "mum"))
        assertEquals(HandOff.Call("Mum", "+44 7700 900003"), phone.handedOff.single())
    }

    @Test
    fun two_people_who_match_are_asked_about() = runTest {
        val failure = assertFailsWith<IllegalStateException> { PhoneActions(FakePhone()).perform("phone.call", args("to" to "Sam")) }
        assertContains(failure.message!!, "Sam Kim, Sam Patel")
    }

    @Test
    fun nobody_by_that_name_or_no_contacts_allowed_say_so() = runTest {
        assertContains(
            assertFailsWith<IllegalStateException> { PhoneActions(FakePhone()).perform("phone.text", args("to" to "Bob", "message" to "hi")) }.message!!,
            "no contact called",
        )
        assertContains(
            assertFailsWith<IllegalStateException> { PhoneActions(FakePhone(book = null)).perform("phone.call", args("to" to "Bob")) }.message!!,
            "isn't allowed",
        )
    }

    @Test
    fun a_number_needs_no_contacts() = runTest {
        val phone = FakePhone(book = null)
        PhoneActions(phone).perform("phone.call", args("to" to "+44 (0)113 496 0000"))
        assertEquals(HandOff.Call("+44 (0)113 496 0000", "+44 (0)113 496 0000"), phone.handedOff.single())
    }

    @Test
    fun with_the_app_in_the_background_its_a_notification_and_the_familiar_is_told() = runTest {
        val result = PhoneActions(FakePhone(inFront = false)).perform("maps.navigate", args("destination" to "Leeds station", "mode" to "walking"))
        assertContains(result["how"]!!.jsonPrimitive.content, "notification")
    }

    @Test
    fun directions_to_a_contact_use_their_address_and_dont_say_it() = runTest {
        val phone = FakePhone()
        val result = PhoneActions(phone).perform("maps.navigate", args("to_contact" to "mum", "mode" to "walking"))

        assertEquals(HandOff.Navigate("1 Oak Lane, Leeds", "walking", shownAs = "Mum's"), phone.handedOff.single())
        assertEquals("Mum", result["to"]!!.jsonPrimitive.content)
        assertFalse(result.toString().contains("Oak Lane"), "The address stays on the phone.")
    }

    @Test
    fun a_contact_with_two_addresses_is_asked_about_unless_one_was_said() = runTest {
        assertContains(
            assertFailsWith<IllegalStateException> { PhoneActions(FakePhone()).perform("maps.navigate", args("to_contact" to "Sam Kim")) }.message!!,
            "home, work",
        )
        val phone = FakePhone()
        PhoneActions(phone).perform("maps.navigate", args("to_contact" to "Sam Kim", "which" to "work"))
        assertEquals("3 Office Park, Leeds", (phone.handedOff.single() as HandOff.Navigate).destination)
    }

    @Test
    fun a_contact_with_no_address_is_said_so() = runTest {
        assertContains(
            assertFailsWith<IllegalStateException> { PhoneActions(FakePhone()).perform("maps.navigate", args("to_contact" to "Sam Patel")) }.message!!,
            "no address",
        )
    }

    @Test
    fun asked_for_a_number_it_says_it_with_its_kind() = runTest {
        val result = PhoneActions(FakePhone()).perform("contacts.read", args("name" to "Mum"))

        assertEquals("Mum", result["name"]!!.jsonPrimitive.content)
        assertContains(result["numbers"].toString(), "+44 7700 900003")
        assertContains(result["numbers"].toString(), "mobile")
        assertEquals(null, result["addresses"])
    }

    @Test
    fun asked_for_a_work_address_it_gives_just_that() = runTest {
        val result = PhoneActions(FakePhone()).perform("contacts.read", args("name" to "Sam Kim", "want" to "address", "which" to "work"))
        val said = result["addresses"].toString()
        assertContains(said, "3 Office Park")
        assertFalse(said.contains("Elm Road"))
    }

    @Test
    fun where_am_i_is_the_place_name() = runTest {
        assertEquals("Headingley, Leeds, England", PhoneActions(FakePhone()).perform("location.read", JsonObject(emptyMap()))["place"]!!.jsonPrimitive.content)
    }
}
