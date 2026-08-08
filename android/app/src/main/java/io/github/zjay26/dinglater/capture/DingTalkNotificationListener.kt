package io.github.zjay26.dinglater.capture

import android.app.Notification
import android.content.ComponentName
import android.content.pm.PackageManager
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import androidx.core.app.NotificationCompat
import io.github.zjay26.dinglater.DingLaterApplication
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import java.time.Instant

class DingTalkNotificationListener : NotificationListenerService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val notificationQueue = Channel<StatusBarNotification>(Channel.UNLIMITED)
    private val mapper = NotificationPayloadMapper()

    private val container get() = (application as DingLaterApplication).container

    override fun onCreate() {
        super.onCreate()
        scope.launch {
            for (notification in notificationQueue) process(notification)
        }
    }

    override fun onListenerConnected() {
        super.onListenerConnected()
        scope.launch { container.preferences.setListenerConnected(true) }
    }

    override fun onListenerDisconnected() {
        scope.launch { container.preferences.setListenerConnected(false) }
        requestRebind(ComponentName(this, DingTalkNotificationListener::class.java))
        super.onListenerDisconnected()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        if (sbn?.packageName != DINGTALK_PACKAGE) return
        if (notificationQueue.trySend(sbn).isFailure) {
            scope.launch {
                runCatching {
                    container.preferences.recordCaptureFailure("NotificationQueueClosed", Instant.now())
                }
            }
        }
    }

    private suspend fun process(sbn: StatusBarNotification) {
        try {
            capture(sbn)
        } catch (exception: Exception) {
            val keyUnavailable = CaptureFailurePolicy.shouldStopCapture(exception)
            runCatching {
                if (keyUnavailable) container.preferences.setCaptureKeyFaulted(true)
                container.preferences.recordCaptureFailure(
                    CaptureFailurePolicy.diagnosticKind(exception),
                    Instant.now()
                )
            }
        }
    }

    private suspend fun capture(sbn: StatusBarNotification) {
        val settings = container.preferences.currentSettings()
        val enabledAt = settings.captureEnabledAt ?: return
        if (!settings.onboardingCompleted || settings.capturePaused || settings.captureFaulted || sbn.postTime < enabledAt.toEpochMilli()) return

        val raw = toRawPayload(sbn)
        val mapped = mapper.map(raw)
        var captured = 0
        var duplicates = 0
        var fingerprintPrefix = container.fingerprints.fingerprint(
            raw.sourcePackage,
            raw.sourceIdentity,
            raw.postTime.toString(),
            raw.payloadShape,
            mapped.titleLength.toString(),
            mapped.bodyLength.toString()
        ).take(12)
        mapped.messages.forEach { message ->
            val result = container.repository.append(message, settings.retentionDays)
            fingerprintPrefix = result.fingerprintPrefix
            if (result.inserted) captured++ else duplicates++
        }
        container.preferences.recordObservation(
            titleLength = mapped.titleLength,
            bodyLength = mapped.bodyLength,
            messageCount = mapped.sourceMessageCount,
            quality = mapped.quality,
            payloadShape = mapped.payloadShape,
            fingerprintPrefix = fingerprintPrefix,
            captured = captured,
            duplicates = duplicates,
            emptyBodies = mapped.emptyBodyCount,
            now = Instant.now()
        )
    }

    private fun toRawPayload(sbn: StatusBarNotification): RawNotificationPayload {
        val notification = sbn.notification
        val extras = notification.extras
        val messagingStyleResult = runCatching {
            NotificationCompat.MessagingStyle.extractMessagingStyleFromNotification(notification)
        }
        val messagingStyle = messagingStyleResult.getOrNull()
        val messagesResult = runCatching {
            messagingStyle?.messages.orEmpty().map { message ->
                RawMessagingMessage(
                    text = message.text?.toString().orEmpty(),
                    sender = message.person?.name?.toString().orEmpty(),
                    timestamp = message.timestamp
                )
            }
        }
        val messages = messagesResult.getOrDefault(emptyList())
        val messagesUnreadable = messagingStyleResult.isFailure || messagesResult.isFailure
        val title = safeText(extras, Notification.EXTRA_TITLE)
        val text = safeText(extras, Notification.EXTRA_TEXT)
        val bigText = safeText(extras, Notification.EXTRA_BIG_TEXT)
        val textLines = runCatching {
            extras.getCharSequenceArray(Notification.EXTRA_TEXT_LINES)?.map(CharSequence::toString).orEmpty()
        }.getOrDefault(emptyList())
        val extrasConversationTitle = safeText(extras, Notification.EXTRA_CONVERSATION_TITLE)
        return RawNotificationPayload(
            sourcePackage = sbn.packageName,
            sourceIdentity = sbn.key,
            postTime = sbn.postTime,
            title = title,
            text = text,
            bigText = bigText,
            textLines = textLines,
            messages = messages,
            conversationTitle = runCatching { messagingStyle?.conversationTitle?.toString() }.getOrNull()
                ?: extrasConversationTitle,
            isGroupConversation = runCatching {
                if (extras.containsKey(Notification.EXTRA_IS_GROUP_CONVERSATION)) {
                    extras.getBoolean(Notification.EXTRA_IS_GROUP_CONVERSATION)
                } else null
            }.getOrNull(),
            isGroupSummary = notification.flags and Notification.FLAG_GROUP_SUMMARY != 0,
            dingTalkVersion = dingTalkVersion(),
            payloadShape = describePayload(
                hasMessages = messagingStyle != null,
                hasMessagesKey = safeContains(extras, Notification.EXTRA_MESSAGES),
                messagesUnreadable = messagesUnreadable,
                hasBigText = bigText.isNotEmpty(),
                hasTextLines = textLines.isNotEmpty(),
                hasText = text.isNotEmpty(),
                hasTitle = title.isNotEmpty(),
                hasConversationTitle = extrasConversationTitle.isNotEmpty()
            )
        )
    }

    private fun safeText(extras: android.os.Bundle, key: String): String =
        runCatching { extras.getCharSequence(key)?.toString().orEmpty() }.getOrDefault("")

    private fun safeContains(extras: android.os.Bundle, key: String): Boolean =
        runCatching { extras.containsKey(key) }.getOrDefault(false)

    private fun describePayload(
        hasMessages: Boolean,
        hasMessagesKey: Boolean,
        messagesUnreadable: Boolean,
        hasBigText: Boolean,
        hasTextLines: Boolean,
        hasText: Boolean,
        hasTitle: Boolean,
        hasConversationTitle: Boolean
    ): String = buildList {
        if (hasMessagesKey || messagesUnreadable) {
            add("messages:${if (messagesUnreadable) "unreadable" else if (hasMessages) "messageArray" else "present"}")
        }
        if (hasBigText) add("bigText:text")
        if (hasTextLines) add("textLines:array")
        if (hasText) add("text:text")
        if (hasTitle) add("title:text")
        if (hasConversationTitle) add("conversationTitle:text")
    }.joinToString(",").ifBlank { "none" }

    private fun dingTalkVersion(): String = try {
        packageManager.getPackageInfo(DINGTALK_PACKAGE, 0).let { info ->
            "${info.versionName.orEmpty()} (${info.longVersionCode})"
        }
    } catch (_: PackageManager.NameNotFoundException) {
        "unknown"
    }

    override fun onDestroy() {
        notificationQueue.close()
        scope.cancel()
        super.onDestroy()
    }

    companion object {
        const val DINGTALK_PACKAGE = "com.alibaba.android.rimet"
    }
}
