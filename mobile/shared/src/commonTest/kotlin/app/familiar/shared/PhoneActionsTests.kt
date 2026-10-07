package app.familiar.shared

import app.familiar.shared.phone.PhoneActions
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
    ) : DevicePhone {
        val handedOff = mutableListOf<HandOff>()

        override suspend fun contacts(name: String) = book?.filter { contact -> contact.name.split(' ').any { it.startsWith(name, ignoreCase = true) } || contact.name.equals(name, ignoreCase = true) }

        override fun handOff(handOff: HandOff): HandedOff {
            handedOff += handOff
            return if (inFront) HandedOff.Opened else HandedOff.Notified
        }

        override suspend fun place() = "Headingley, Leeds, England"
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
    fun where_am_i_is_the_place_name() = runTest {
        assertEquals("Headingley, Leeds, England", PhoneActions(FakePhone()).perform("location.read", JsonObject(emptyMap()))["place"]!!.jsonPrimitive.content)
    }
}
