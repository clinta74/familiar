package app.familiar.shared.auth

import app.familiar.shared.platform.SecureStore

/** A signed-in device: which server, and the bearer token it was given there. */
data class SignedIn(val server: String, val token: String, val deviceId: String, val username: String)

/** The device's sign-in, kept in the secure store across launches. */
class Session(private val store: SecureStore) {
    fun current(): SignedIn? {
        val server = store.read(SERVER) ?: return null
        val token = store.read(TOKEN) ?: return null
        return SignedIn(server, token, store.read(DEVICE).orEmpty(), store.read(USERNAME).orEmpty())
    }

    /** The server last signed in to, to fill in next time. */
    fun lastServer(): String? = store.read(SERVER)

    fun save(signedIn: SignedIn) {
        store.write(SERVER, signedIn.server)
        store.write(TOKEN, signedIn.token)
        store.write(DEVICE, signedIn.deviceId)
        store.write(USERNAME, signedIn.username)
    }

    /** Forgets the token; the server address stays, for signing in again. */
    fun clear() {
        store.write(TOKEN, null)
        store.write(DEVICE, null)
    }

    private companion object {
        const val SERVER = "server"
        const val TOKEN = "token"
        const val DEVICE = "device"
        const val USERNAME = "username"
    }
}
