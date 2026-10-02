package app.familiar.shared.platform

import platform.Foundation.NSTimeZone
import platform.Foundation.localTimeZone

// Compiled on macOS only (CI). The Keychain-backed SecureStore arrives with the iOS app (docs/MOBILE.md M0/M5).

actual fun currentTimeZoneId(): String = NSTimeZone.localTimeZone.name
