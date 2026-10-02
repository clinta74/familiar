package app.familiar.android

import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import app.familiar.shared.platform.KeystoreStore
import app.familiar.shared.ui.FamiliarApp

/** The app's one screen; everything inside is the shared Compose UI (mobile/shared). */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        val store = KeystoreStore(applicationContext)
        val deviceName = listOf(Build.MANUFACTURER.replaceFirstChar { it.uppercase() }, Build.MODEL)
            .distinct()
            .joinToString(" ")
            .take(64)
        setContent { FamiliarApp(store, deviceName) }
    }
}
