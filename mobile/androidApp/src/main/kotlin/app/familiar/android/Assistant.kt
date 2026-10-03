package app.familiar.android

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.service.quicksettings.TileService
import android.service.voice.VoiceInteractionService
import android.service.voice.VoiceInteractionSession
import android.service.voice.VoiceInteractionSessionService
import android.speech.RecognitionService

/**
 * Familiar as the phone's assistant (docs/MOBILE.md, M4): once it's chosen in Settings > Default apps
 * > Digital assistant app, long-pressing home (or the assist gesture, or a headset's assist button)
 * opens the familiar listening. Android only offers apps with a voice interaction service there,
 * and that service needs a session service and a recognition service to be valid.
 */
class FamiliarAssistantService : VoiceInteractionService()

class FamiliarAssistSessionService : VoiceInteractionSessionService() {
    override fun onNewSession(args: Bundle?): VoiceInteractionSession = FamiliarAssistSession(this)
}

/** The assist gesture: no overlay of our own - straight into the app, listening. */
class FamiliarAssistSession(context: Context) : VoiceInteractionSession(context) {
    override fun onShow(args: Bundle?, showFlags: Int) {
        super.onShow(args, showFlags)
        startAssistantActivity(Talk.intent(context))
        hide()
    }
}

/**
 * Required by the assistant role, but speech is recognised on the server, not offered to other
 * apps: anything asking this to recognise is told it can't.
 */
class FamiliarRecognitionService : RecognitionService() {
    override fun onStartListening(recognizerIntent: Intent?, listener: Callback?) {
        listener?.error(android.speech.SpeechRecognizer.ERROR_CLIENT)
    }

    override fun onStopListening(listener: Callback?) = Unit

    override fun onCancel(listener: Callback?) = Unit
}

/** The Quick Settings tile: tap to talk. */
class TalkTileService : TileService() {
    override fun onClick() {
        super.onClick()
        val intent = Talk.intent(this)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE))
        } else {
            @Suppress("DEPRECATION", "StartActivityAndCollapseDeprecated")
            startActivityAndCollapse(intent)
        }
    }
}

/** Opening the app to talk: the assistant, the tile, the shortcut and the widget all use this. */
object Talk {
    /**
     * Its own action, not just an extra: Android matches pending intents without their extras, so a
     * plain "open the app" one (the hands-free notification's) would otherwise stand in for it.
     */
    const val ACTION = "app.familiar.TALK"
    const val EXTRA_LISTEN = "app.familiar.LISTEN"

    fun intent(context: Context): Intent =
        Intent(ACTION, null, context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .putExtra(EXTRA_LISTEN, true)

    /** Whether [intent] opened the app to talk. */
    fun asked(intent: Intent?): Boolean =
        intent != null && (intent.action == ACTION || intent.action == Intent.ACTION_ASSIST || intent.getBooleanExtra(EXTRA_LISTEN, false))
}
