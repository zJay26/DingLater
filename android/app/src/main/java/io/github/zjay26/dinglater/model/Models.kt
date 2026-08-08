package io.github.zjay26.dinglater.model

import java.time.Instant

enum class InboxState { INBOX, SNOOZED, HANDLED }

enum class PayloadQuality(val label: String, val potentiallyIncomplete: Boolean) {
    MESSAGING_STYLE("消息通知", false),
    BIG_TEXT("展开通知", false),
    TEXT_LINES("通知摘要", true),
    VISIBLE_TEXT("可见通知", true)
}

enum class CaptureHealthState { STOPPED, PAUSED, HEALTHY, PERMISSION_DENIED, UNAVAILABLE, FAULTED }

data class CapturedMessage(
    val capturedAt: Instant,
    val messageAt: Instant?,
    val conversation: String,
    val sender: String,
    val visibleBody: String,
    val quality: PayloadQuality,
    val sourcePackage: String,
    val sourceIdentity: String,
    val dingTalkVersion: String,
    val conversationIsGroup: Boolean?
)

data class StoredMessage(
    val id: String,
    val captured: CapturedMessage,
    val state: InboxState,
    val expiresAt: Instant,
    val snoozedUntil: Instant?,
    val updatedAt: Instant
)

data class AppSettings(
    val onboardingCompleted: Boolean = false,
    val captureEnabledAt: Instant? = null,
    val capturePaused: Boolean = false,
    val retentionDays: Int = 7,
    val showReminderPreview: Boolean = false,
    val quickSnoozeMinutes: Int = 30,
    val listenerConnected: Boolean = false,
    val captureFaulted: Boolean = false
)

data class CaptureDiagnostics(
    val observationCount: Long = 0,
    val capturedCount: Long = 0,
    val duplicateCount: Long = 0,
    val emptyBodyCount: Long = 0,
    val lastObservationAt: Instant? = null,
    val lastQuality: PayloadQuality? = null,
    val lastTitleLength: Int = 0,
    val lastBodyLength: Int = 0,
    val lastMessageCount: Int = 0,
    val lastFingerprintPrefix: String = "",
    val lastPayloadShape: String = "",
    val failureCount: Long = 0,
    val lastFailureAt: Instant? = null,
    val lastFailureKind: String = ""
)

data class CaptureHealth(
    val state: CaptureHealthState,
    val detail: String,
    val notificationAccessGranted: Boolean,
    val reminderPermissionGranted: Boolean,
    val dingTalkDetected: Boolean,
    val listenerConnected: Boolean
)
