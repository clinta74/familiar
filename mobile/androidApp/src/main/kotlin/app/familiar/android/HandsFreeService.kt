package app.familiar.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.os.IBinder
import android.os.PowerManager
import android.view.KeyEvent
import app.familiar.shared.session.FamiliarSessions

/**
 * Keeps hands-free listening with the screen off (docs/MOBILE.md, M2): a foreground service of type
 * microphone, with a notification that says so and stops it. It owns no audio - the session's
 * hands-free listener does - it only keeps the app allowed to use the microphone, holds the CPU
 * awake while listening, and takes the headset's button: pressing it while the familiar is talking
 * stops it, as talking over it would.
 */
class HandsFreeService : Service() {
    private var media: MediaSession? = null
    private var wake: PowerManager.WakeLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            // From the notification: switch hands-free off in the app too (which stops this).
            AppServices.handsFree?.stoppedOutside()
            stopSelf()
            return START_NOT_STICKY
        }
        startForeground(NOTIFICATION, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
        if (wake == null) {
            wake = (getSystemService(POWER_SERVICE) as PowerManager)
                .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "familiar:hands-free")
                .apply { acquire(MAX_LISTENING_MS) }
        }
        if (media == null) media = headsetButton()
        // Not restarted by the system after being killed: the microphone needs the app in front to
        // start again, so it's switched back on from the app.
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        media?.release()
        media = null
        wake?.takeIf { it.isHeld }?.release()
        wake = null
        super.onDestroy()
    }

    private fun notification(): Notification {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL, "Hands-free listening", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Shown while Familiar is listening for you to talk."
                setShowBadge(false)
            },
        )
        val open = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE,
        )
        val stop = PendingIntent.getService(
            this, 1,
            Intent(this, HandsFreeService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE,
        )
        val name = FamiliarSessions.current?.controller?.state?.value?.familiarName ?: "Familiar"
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle("$name is listening")
            .setContentText("Just talk. Tap to open.")
            .setContentIntent(open)
            .setOngoing(true)
            .setCategory(Notification.CATEGORY_SERVICE)
            .addAction(Notification.Action.Builder(null, "Stop listening", stop).build())
            .build()
    }

    /** The headset's play/pause button: stops the familiar talking. */
    private fun headsetButton() = MediaSession(this, "familiar-hands-free").apply {
        setCallback(object : MediaSession.Callback() {
            override fun onMediaButtonEvent(intent: Intent): Boolean {
                @Suppress("DEPRECATION")
                val key = intent.getParcelableExtra<KeyEvent>(Intent.EXTRA_KEY_EVENT) ?: return false
                if (key.action != KeyEvent.ACTION_DOWN) return true
                return when (key.keyCode) {
                    KeyEvent.KEYCODE_HEADSETHOOK, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE,
                    KeyEvent.KEYCODE_MEDIA_PAUSE, KeyEvent.KEYCODE_MEDIA_PLAY, KeyEvent.KEYCODE_MEDIA_STOP -> {
                        FamiliarSessions.current?.let { if (it.busy) it.stopReply() }
                        true
                    }
                    else -> false
                }
            }
        })
        // Buttons go to the session that's playing; while listening, that's this one.
        setPlaybackState(
            PlaybackState.Builder()
                .setActions(PlaybackState.ACTION_PLAY_PAUSE or PlaybackState.ACTION_STOP)
                .setState(PlaybackState.STATE_PLAYING, 0, 1f)
                .build(),
        )
        isActive = true
    }

    companion object {
        private const val CHANNEL = "hands-free"
        private const val NOTIFICATION = 1
        private const val ACTION_STOP = "app.familiar.HANDS_FREE_STOP"

        /** A long conversation, but not a phone left listening for ever by mistake. */
        private const val MAX_LISTENING_MS = 4 * 60 * 60 * 1000L

        fun start(context: Context) {
            context.startForegroundService(Intent(context, HandsFreeService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, HandsFreeService::class.java))
        }
    }
}
