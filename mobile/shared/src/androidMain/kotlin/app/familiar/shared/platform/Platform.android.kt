package app.familiar.shared.platform

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.TimeZone
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

actual fun currentTimeZoneId(): String = TimeZone.getDefault().id

actual fun formatInstant(iso: String, withTime: Boolean, utc: Boolean): String = runCatching {
    val zone: ZoneId = if (utc) ZoneOffset.UTC else ZoneId.systemDefault()
    val format = if (withTime) DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT) else DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM)
    // Not Instant.parse: before Android 13 its java.time won't take an offset ("+00:00"), which .NET writes.
    format.format(OffsetDateTime.parse(iso).atZoneSameInstant(zone))
}.getOrDefault(iso)

/**
 * Values encrypted with an AES key that lives in the Android Keystore (it never leaves the secure
 * hardware where there is some), stored in ordinary preferences. Someone reading the app's files
 * gets ciphertext.
 */
class KeystoreStore(context: Context) : SecureStore {
    private val prefs = context.getSharedPreferences("familiar.secure", Context.MODE_PRIVATE)

    override fun read(key: String): String? {
        val stored = prefs.getString(key, null) ?: return null
        return try {
            val bytes = Base64.decode(stored, Base64.NO_WRAP)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes, 0, IV_BYTES))
            String(cipher.doFinal(bytes, IV_BYTES, bytes.size - IV_BYTES), Charsets.UTF_8)
        } catch (_: Exception) {
            // The key is gone (app data restored to another phone, say): as good as signed out.
            prefs.edit().remove(key).apply()
            null
        }
    }

    override fun write(key: String, value: String?) {
        if (value == null) {
            prefs.edit().remove(key).apply()
            return
        }

        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val encrypted = cipher.iv + cipher.doFinal(value.toByteArray(Charsets.UTF_8))
        prefs.edit().putString(key, Base64.encodeToString(encrypted, Base64.NO_WRAP)).apply()
    }

    private fun key(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (keyStore.getEntry(ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val ALIAS = "familiar.secure-store"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_BYTES = 12
    }
}
