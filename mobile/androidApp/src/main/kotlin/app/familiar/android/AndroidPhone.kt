package app.familiar.android

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.location.Address
import android.location.Geocoder
import android.location.Location
import android.location.LocationManager
import android.net.Uri
import android.os.Build
import android.provider.ContactsContract
import app.familiar.shared.platform.DevicePhone
import app.familiar.shared.platform.HandOff
import app.familiar.shared.platform.HandedOff
import app.familiar.shared.platform.PhoneContact
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.util.Locale
import java.util.function.Consumer
import kotlin.coroutines.resume

/**
 * The phone's contacts, Messages, dialler, Maps and location for the familiar (BACKLOG item 21).
 * Nothing is sent or rung from here: Messages opens with the text written and the dialler with the
 * number in, and the user taps Send or Call - so no SEND_SMS or CALL_PHONE permission. Contacts and
 * location are asked for the first time they're needed, when the app is on screen to ask.
 */
class AndroidPhone(context: Context, private val ask: suspend (Array<String>) -> Unit) : DevicePhone {
    private val context = context.applicationContext

    override suspend fun contacts(name: String): List<PhoneContact>? {
        if (!allowed(Manifest.permission.READ_CONTACTS)) return null
        return withContext(Dispatchers.IO) {
            val uri = Uri.withAppendedPath(ContactsContract.CommonDataKinds.Phone.CONTENT_FILTER_URI, Uri.encode(name))
            val columns = arrayOf(
                ContactsContract.CommonDataKinds.Phone.DISPLAY_NAME,
                ContactsContract.CommonDataKinds.Phone.NUMBER,
                ContactsContract.CommonDataKinds.Phone.TYPE,
            )
            context.contentResolver.query(uri, columns, null, null, null)?.use { rows ->
                buildList {
                    while (rows.moveToNext() && size < 30) {
                        val number = rows.getString(1) ?: continue
                        add(PhoneContact(rows.getString(0) ?: number, number, rows.getInt(2) == ContactsContract.CommonDataKinds.Phone.TYPE_MOBILE))
                    }
                }.distinctBy { it.name to it.number.filter(Char::isDigit) }
            } ?: emptyList()
        }
    }

    override fun handOff(handOff: HandOff): HandedOff {
        val (intent, title, body) = when (handOff) {
            is HandOff.Text -> Triple(
                // Google Messages reads the text from the address (?body=), others from sms_body: both.
                Intent(Intent.ACTION_SENDTO, Uri.parse("smsto:" + Uri.encode(handOff.number) + "?body=" + Uri.encode(handOff.message)))
                    .putExtra("sms_body", handOff.message),
                "Text ${handOff.to}",
                handOff.message,
            )
            is HandOff.Call -> Triple(Intent(Intent.ACTION_DIAL, Uri.parse("tel:" + Uri.encode(handOff.number))), "Call ${handOff.to}", "Tap to open the dialler.")
            is HandOff.Navigate -> Triple(
                // The universal Maps link: Google Maps if it's there, the browser if not.
                Intent(Intent.ACTION_VIEW, Uri.parse(
                    "https://www.google.com/maps/dir/?api=1&destination=" + Uri.encode(handOff.destination) +
                        "&travelmode=" + travelMode(handOff.mode) + "&dir_action=navigate",
                )),
                "Directions to ${handOff.destination}",
                "Tap to open Maps.",
            )
        }
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

        if (AppServices.inFront) {
            try {
                context.startActivity(intent)
                return HandedOff.Opened
            } catch (_: ActivityNotFoundException) {
                throw IllegalStateException("This phone has no app for that.")
            } catch (_: SecurityException) {
                // Not allowed to open it after all (the app went to the background just now): offer it instead.
            }
        }
        notify(title, body, intent)
        return HandedOff.Notified
    }

    override suspend fun place(): String {
        if (!allowed(Manifest.permission.ACCESS_COARSE_LOCATION)) {
            throw IllegalStateException("Familiar isn't allowed to know where this phone is. Ask them to allow location in the app.")
        }
        val location = locate() ?: throw IllegalStateException("The phone couldn't tell where it is just now (location may be off).")
        return name(location)
            ?: String.format(Locale.ROOT, "near %.2f, %.2f (no place name available)", location.latitude, location.longitude)
    }

    /** Granted already, or asked now - only possible with the app on screen. */
    private suspend fun allowed(permission: String): Boolean {
        if (granted(permission)) return true
        if (!AppServices.inFront) return false
        ask(arrayOf(permission))
        return granted(permission)
    }

    private fun granted(permission: String) = context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED

    @Suppress("MissingPermission") // Checked in place().
    private suspend fun locate(): Location? {
        val manager = context.getSystemService(LocationManager::class.java)
        val providers = listOf(LocationManager.NETWORK_PROVIDER, LocationManager.GPS_PROVIDER).filter { runCatching { manager.isProviderEnabled(it) }.getOrDefault(false) }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            for (provider in providers) {
                val fresh = withTimeoutOrNull(8_000) {
                    suspendCancellableCoroutine { done ->
                        manager.getCurrentLocation(provider, null, context.mainExecutor, Consumer { done.resume(it) })
                    }
                }
                if (fresh != null) return fresh
            }
        }
        return providers.mapNotNull { manager.getLastKnownLocation(it) }.maxByOrNull { it.time }
    }

    /** "Headingley, Leeds, England": the phone's own geocoder, so the coordinates don't leave it. */
    private suspend fun name(location: Location): String? {
        if (!Geocoder.isPresent()) return null
        val geocoder = Geocoder(context, Locale.getDefault())
        val address: Address? = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            withTimeoutOrNull(8_000) {
                suspendCancellableCoroutine { done ->
                    geocoder.getFromLocation(location.latitude, location.longitude, 1, object : Geocoder.GeocodeListener {
                        override fun onGeocode(addresses: MutableList<Address>) = done.resume(addresses.firstOrNull())
                        override fun onError(errorMessage: String?) = done.resume(null)
                    })
                }
            }
        } else {
            withContext(Dispatchers.IO) {
                @Suppress("DEPRECATION")
                runCatching { geocoder.getFromLocation(location.latitude, location.longitude, 1)?.firstOrNull() }.getOrNull()
            }
        }
        return address?.let { listOfNotNull(it.subLocality, it.locality, it.adminArea, it.countryName).distinct().joinToString(", ") }?.ifBlank { null }
    }

    private fun notify(title: String, body: String, intent: Intent) {
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL, "Ready to send or call", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "A text, call or directions your familiar got ready while the app wasn't on screen."
            },
        )
        val id = (title + body).hashCode()
        val open = PendingIntent.getActivity(context, id, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        manager.notify(
            id,
            Notification.Builder(context, CHANNEL)
                .setSmallIcon(R.drawable.ic_launcher_foreground)
                .setContentTitle(title)
                .setContentText(body)
                .setStyle(Notification.BigTextStyle().bigText(body))
                .setContentIntent(open)
                .setAutoCancel(true)
                .build(),
        )
    }

    private companion object {
        const val CHANNEL = "handoff"

        fun travelMode(mode: String) = when (mode) {
            "walking" -> "walking"
            "cycling" -> "bicycling"
            "transit" -> "transit"
            else -> "driving"
        }
    }
}
