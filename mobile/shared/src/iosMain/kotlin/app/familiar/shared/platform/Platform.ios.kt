package app.familiar.shared.platform

import platform.Foundation.NSDateFormatter
import platform.Foundation.NSDateFormatterMediumStyle
import platform.Foundation.NSDateFormatterNoStyle
import platform.Foundation.NSDateFormatterShortStyle
import platform.Foundation.NSISO8601DateFormatter
import platform.Foundation.NSISO8601DateFormatWithFractionalSeconds
import platform.Foundation.NSISO8601DateFormatWithInternetDateTime
import platform.Foundation.NSTimeZone
import platform.Foundation.localTimeZone
import platform.Foundation.timeZoneForSecondsFromGMT

// Compiled on macOS only (CI). The Keychain-backed SecureStore arrives with the iOS app (docs/MOBILE.md M0/M5).

actual fun currentTimeZoneId(): String = NSTimeZone.localTimeZone.name

actual fun formatInstant(iso: String, withTime: Boolean, utc: Boolean): String {
    val parser = NSISO8601DateFormatter()
    val date = parser.dateFromString(iso)
        ?: parser.apply { formatOptions = NSISO8601DateFormatWithInternetDateTime or NSISO8601DateFormatWithFractionalSeconds }.dateFromString(iso)
        ?: return iso
    return NSDateFormatter().apply {
        dateStyle = NSDateFormatterMediumStyle
        timeStyle = if (withTime) NSDateFormatterShortStyle else NSDateFormatterNoStyle
        if (utc) timeZone = NSTimeZone.timeZoneForSecondsFromGMT(0)
    }.stringFromDate(date)
}
