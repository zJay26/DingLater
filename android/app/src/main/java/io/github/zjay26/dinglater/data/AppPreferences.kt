package io.github.zjay26.dinglater.data

import android.content.Context
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import io.github.zjay26.dinglater.model.AppSettings
import io.github.zjay26.dinglater.model.CaptureDiagnostics
import io.github.zjay26.dinglater.model.PayloadQuality
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import java.time.Instant

private val Context.dingLaterDataStore by preferencesDataStore(name = "dinglater_settings")

class AppPreferences(private val context: Context) {
    val settings: Flow<AppSettings> = context.dingLaterDataStore.data.map(::toSettings)
    val diagnostics: Flow<CaptureDiagnostics> = context.dingLaterDataStore.data.map(::toDiagnostics)

    suspend fun currentSettings(): AppSettings = settings.first()

    suspend fun completeOnboarding(now: Instant) {
        context.dingLaterDataStore.edit { values ->
            values[ONBOARDING] = true
            if (!values.contains(CAPTURE_ENABLED_AT)) values[CAPTURE_ENABLED_AT] = now.toEpochMilli()
        }
    }

    suspend fun setCapturePaused(paused: Boolean) = update(CAPTURE_PAUSED, paused)

    suspend fun setRetentionDays(days: Int) = update(RETENTION_DAYS, days.coerceIn(1, 365))

    suspend fun setReminderPreview(enabled: Boolean) = update(REMINDER_PREVIEW, enabled)

    suspend fun setQuickSnoozeMinutes(minutes: Int) = update(QUICK_SNOOZE, minutes.coerceIn(1, 1440))

    suspend fun setListenerConnected(connected: Boolean) = update(LISTENER_CONNECTED, connected)

    suspend fun setCaptureKeyFaulted(faulted: Boolean) = update(CAPTURE_KEY_FAULTED, faulted)

    suspend fun resetAll() {
        context.dingLaterDataStore.edit { it.clear() }
    }

    suspend fun recordObservation(
        titleLength: Int,
        bodyLength: Int,
        messageCount: Int,
        quality: PayloadQuality?,
        payloadShape: String,
        fingerprintPrefix: String,
        captured: Int,
        duplicates: Int,
        emptyBodies: Int,
        now: Instant
    ) {
        context.dingLaterDataStore.edit { values ->
            values[OBSERVATION_COUNT] = (values[OBSERVATION_COUNT] ?: 0L) + 1
            values[CAPTURED_COUNT] = (values[CAPTURED_COUNT] ?: 0L) + captured
            values[DUPLICATE_COUNT] = (values[DUPLICATE_COUNT] ?: 0L) + duplicates
            values[EMPTY_BODY_COUNT] = (values[EMPTY_BODY_COUNT] ?: 0L) + emptyBodies
            values[LAST_OBSERVATION_AT] = now.toEpochMilli()
            values[LAST_TITLE_LENGTH] = titleLength
            values[LAST_BODY_LENGTH] = bodyLength
            values[LAST_MESSAGE_COUNT] = messageCount
            values[LAST_QUALITY] = quality?.name.orEmpty()
            values[LAST_PAYLOAD_SHAPE] = payloadShape.take(240)
            values[LAST_FINGERPRINT_PREFIX] = fingerprintPrefix.take(12)
        }
    }

    suspend fun recordCaptureFailure(kind: String, now: Instant) {
        context.dingLaterDataStore.edit { values ->
            values[FAILURE_COUNT] = (values[FAILURE_COUNT] ?: 0L) + 1
            values[LAST_FAILURE_AT] = now.toEpochMilli()
            values[LAST_FAILURE_KIND] = kind.take(80)
        }
    }

    suspend fun clearDiagnostics() {
        context.dingLaterDataStore.edit { values ->
            values.remove(OBSERVATION_COUNT)
            values.remove(CAPTURED_COUNT)
            values.remove(DUPLICATE_COUNT)
            values.remove(EMPTY_BODY_COUNT)
            values.remove(LAST_OBSERVATION_AT)
            values.remove(LAST_QUALITY)
            values.remove(LAST_PAYLOAD_SHAPE)
            values.remove(LAST_TITLE_LENGTH)
            values.remove(LAST_BODY_LENGTH)
            values.remove(LAST_MESSAGE_COUNT)
            values.remove(LAST_FINGERPRINT_PREFIX)
            values.remove(FAILURE_COUNT)
            values.remove(LAST_FAILURE_AT)
            values.remove(LAST_FAILURE_KIND)
        }
    }

    private suspend fun <T> update(key: Preferences.Key<T>, value: T) {
        context.dingLaterDataStore.edit { it[key] = value }
    }

    private fun toSettings(values: Preferences) = AppSettings(
        onboardingCompleted = values[ONBOARDING] ?: false,
        captureEnabledAt = values[CAPTURE_ENABLED_AT]?.let(Instant::ofEpochMilli),
        capturePaused = values[CAPTURE_PAUSED] ?: false,
        retentionDays = (values[RETENTION_DAYS] ?: 7).coerceIn(1, 365),
        showReminderPreview = values[REMINDER_PREVIEW] ?: false,
        quickSnoozeMinutes = (values[QUICK_SNOOZE] ?: 30).coerceIn(1, 1440),
        listenerConnected = values[LISTENER_CONNECTED] ?: false,
        captureFaulted = values[CAPTURE_KEY_FAULTED] ?: false
    )

    private fun toDiagnostics(values: Preferences) = CaptureDiagnostics(
        observationCount = values[OBSERVATION_COUNT] ?: 0,
        capturedCount = values[CAPTURED_COUNT] ?: 0,
        duplicateCount = values[DUPLICATE_COUNT] ?: 0,
        emptyBodyCount = values[EMPTY_BODY_COUNT] ?: 0,
        lastObservationAt = values[LAST_OBSERVATION_AT]?.let(Instant::ofEpochMilli),
        lastQuality = values[LAST_QUALITY]?.takeIf(String::isNotBlank)?.let {
            runCatching { PayloadQuality.valueOf(it) }.getOrNull()
        },
        lastTitleLength = values[LAST_TITLE_LENGTH] ?: 0,
        lastBodyLength = values[LAST_BODY_LENGTH] ?: 0,
        lastMessageCount = values[LAST_MESSAGE_COUNT] ?: 0,
        lastFingerprintPrefix = values[LAST_FINGERPRINT_PREFIX].orEmpty(),
        lastPayloadShape = values[LAST_PAYLOAD_SHAPE].orEmpty(),
        failureCount = values[FAILURE_COUNT] ?: 0,
        lastFailureAt = values[LAST_FAILURE_AT]?.let(Instant::ofEpochMilli),
        lastFailureKind = values[LAST_FAILURE_KIND].orEmpty()
    )

    companion object {
        private val ONBOARDING = booleanPreferencesKey("onboarding_completed")
        private val CAPTURE_ENABLED_AT = longPreferencesKey("capture_enabled_at")
        private val CAPTURE_PAUSED = booleanPreferencesKey("capture_paused")
        private val RETENTION_DAYS = intPreferencesKey("retention_days")
        private val REMINDER_PREVIEW = booleanPreferencesKey("reminder_preview")
        private val QUICK_SNOOZE = intPreferencesKey("quick_snooze_minutes")
        private val LISTENER_CONNECTED = booleanPreferencesKey("listener_connected")
        // v1 accidentally treated every transient parse/storage error as a permanent key fault.
        // A new key intentionally discards that sticky state when users install this fix.
        private val CAPTURE_KEY_FAULTED = booleanPreferencesKey("capture_key_faulted_v2")

        private val OBSERVATION_COUNT = longPreferencesKey("diagnostics_observation_count")
        private val CAPTURED_COUNT = longPreferencesKey("diagnostics_captured_count")
        private val DUPLICATE_COUNT = longPreferencesKey("diagnostics_duplicate_count")
        private val EMPTY_BODY_COUNT = longPreferencesKey("diagnostics_empty_body_count")
        private val LAST_OBSERVATION_AT = longPreferencesKey("diagnostics_last_observation_at")
        private val LAST_QUALITY = stringPreferencesKey("diagnostics_last_quality")
        private val LAST_PAYLOAD_SHAPE = stringPreferencesKey("diagnostics_last_payload_shape")
        private val LAST_TITLE_LENGTH = intPreferencesKey("diagnostics_last_title_length")
        private val LAST_BODY_LENGTH = intPreferencesKey("diagnostics_last_body_length")
        private val LAST_MESSAGE_COUNT = intPreferencesKey("diagnostics_last_message_count")
        private val LAST_FINGERPRINT_PREFIX = stringPreferencesKey("diagnostics_last_fingerprint")
        private val FAILURE_COUNT = longPreferencesKey("diagnostics_failure_count")
        private val LAST_FAILURE_AT = longPreferencesKey("diagnostics_last_failure_at")
        private val LAST_FAILURE_KIND = stringPreferencesKey("diagnostics_last_failure_kind")
    }
}
