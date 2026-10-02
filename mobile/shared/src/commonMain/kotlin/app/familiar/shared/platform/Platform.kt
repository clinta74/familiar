package app.familiar.shared.platform

/** The user's IANA time zone ("Europe/London"), for the situation the server gives the model. */
expect fun currentTimeZoneId(): String

/**
 * Where the access token and server address are kept: the Android Keystore, the iOS Keychain - never
 * plain preferences (docs/MOBILE.md §1).
 */
interface SecureStore {
    fun read(key: String): String?

    /** Writes, or with null, removes. */
    fun write(key: String, value: String?)
}

/** A store in memory, for previews and tests. */
class InMemoryStore : SecureStore {
    private val values = mutableMapOf<String, String>()

    override fun read(key: String) = values[key]

    override fun write(key: String, value: String?) {
        if (value == null) values.remove(key) else values[key] = value
    }
}
