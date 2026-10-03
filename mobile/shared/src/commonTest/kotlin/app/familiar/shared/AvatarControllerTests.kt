package app.familiar.shared

import app.familiar.shared.avatar.AvatarController
import app.familiar.shared.avatar.TimedWord
import app.familiar.shared.protocol.ServerEvent
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlin.test.Test
import kotlin.test.assertEquals

class AvatarControllerTests {
    @Test
    fun stream_events_become_page_calls() = runTest {
        val avatar = AvatarController()
        val calls = mutableListOf<String>()
        backgroundScope.launch(UnconfinedTestDispatcher(testScheduler)) { avatar.calls.toList(calls) }

        avatar.handle(ServerEvent.Expression("joy", 0.72))
        avatar.handle(ServerEvent.Gesture("wave"))
        avatar.handle(ServerEvent.ReplyDelta("r1", "Hello \"you\""))
        avatar.handle(ServerEvent.ReplyDelta("r1", "unheard"), audible = true)
        avatar.state("thinking")
        avatar.state("thinking")
        avatar.clip(listOf(TimedWord("Hi", 0.0, 0.25)), 40)
        avatar.clip(null, 0)

        assertEquals(
            listOf(
                """familiarAvatar.expression("joy",0.72)""",
                """familiarAvatar.gesture("wave")""",
                """familiarAvatar.text("Hello \"you\"")""",
                """familiarAvatar.state("thinking")""",
                """familiarAvatar.clip([{"word":"Hi","start":0.0,"end":0.25}],40)""",
                """familiarAvatar.clip(null,0)""",
            ),
            calls,
        )
    }

    @Test
    fun a_reloaded_page_catches_up_on_the_look_and_state() {
        val avatar = AvatarController()
        assertEquals(listOf("""familiarAvatar.state("idle")"""), avatar.snapshot())

        avatar.look(JsonObject(mapOf("model" to JsonPrimitive("hedgehog"))))
        avatar.state("speaking")
        assertEquals(
            listOf("""familiarAvatar.look({"model":"hedgehog"})""", """familiarAvatar.state("speaking")"""),
            avatar.snapshot(),
        )
    }
}
