package app.ember.habits

import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneOffset
import java.util.Locale
import java.util.TimeZone

/**
 * A device zone whose date differs from UTC's, at least an hour from its own midnight: one always exists between
 * UTC-12 and UTC+14, so a test can tell the device's day from UTC's at any time.
 */
class DeviceZone {

    val utcToday: String = LocalDate.now(ZoneOffset.UTC).toString()
    val id: String
    val today: String

    init {
        val (offset, now) = (-12..14)
            .map { it to LocalDateTime.now(ZoneOffset.ofHours(it)) }
            .first { (_, now) -> now.hour in 1..22 && now.toLocalDate().toString() != utcToday }
        id = String.format(Locale.ROOT, "GMT%+03d:00", offset)
        today = now.toLocalDate().toString()
    }

    /** Runs [block] with this zone as the device's default zone, then restores the previous one. */
    fun <T> use(block: () -> T): T {
        val previous = TimeZone.getDefault()
        TimeZone.setDefault(TimeZone.getTimeZone(id))
        try {
            return block()
        } finally {
            TimeZone.setDefault(previous)
        }
    }
}
