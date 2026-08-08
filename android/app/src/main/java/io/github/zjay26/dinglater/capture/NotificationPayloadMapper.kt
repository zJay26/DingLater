package io.github.zjay26.dinglater.capture

import io.github.zjay26.dinglater.model.CapturedMessage
import io.github.zjay26.dinglater.model.PayloadQuality
import java.time.Instant

data class RawMessagingMessage(val text: String, val sender: String, val timestamp: Long)

data class RawNotificationPayload(
    val sourcePackage: String,
    val sourceIdentity: String,
    val postTime: Long,
    val title: String,
    val text: String,
    val bigText: String,
    val textLines: List<String>,
    val messages: List<RawMessagingMessage>,
    val conversationTitle: String,
    val isGroupConversation: Boolean?,
    val isGroupSummary: Boolean,
    val dingTalkVersion: String,
    val payloadShape: String = ""
)

data class MappedNotification(
    val messages: List<CapturedMessage>,
    val quality: PayloadQuality?,
    val titleLength: Int,
    val bodyLength: Int,
    val sourceMessageCount: Int,
    val emptyBodyCount: Int,
    val payloadShape: String
)

class NotificationPayloadMapper {
    fun map(payload: RawNotificationPayload, observedAt: Instant = Instant.now()): MappedNotification {
        if (payload.isGroupSummary) return empty(payload)

        val conversation = payload.conversationTitle.ifBlank { payload.title }.ifBlank { "未命名会话" }
        var emptyMessagingBodies = 0
        if (payload.messages.isNotEmpty()) {
            val mapped = payload.messages.mapIndexedNotNull { index, message ->
                val body = message.text.trim()
                if (body.isEmpty()) return@mapIndexedNotNull null
                captured(
                    payload = payload,
                    observedAt = observedAt,
                    conversation = conversation,
                    sender = message.sender.ifBlank { payload.title }.ifBlank { "发送者未知" },
                    body = body,
                    quality = PayloadQuality.MESSAGING_STYLE,
                    identitySuffix = "${message.timestamp}:$index",
                    messageAt = message.timestamp.takeIf { it > 0 }?.let(Instant::ofEpochMilli)
                )
            }
            if (mapped.isNotEmpty()) {
                return MappedNotification(
                    messages = mapped,
                    quality = PayloadQuality.MESSAGING_STYLE,
                    titleLength = payload.title.length,
                    bodyLength = mapped.sumOf { it.visibleBody.length },
                    sourceMessageCount = payload.messages.size,
                    emptyBodyCount = payload.messages.size - mapped.size,
                    payloadShape = payload.payloadShape
                )
            }
            emptyMessagingBodies = payload.messages.size
        }

        val selected = when {
            payload.bigText.isNotBlank() -> payload.bigText to PayloadQuality.BIG_TEXT
            payload.textLines.any(String::isNotBlank) -> payload.textLines.filter(String::isNotBlank).joinToString("\n") { it.trim() } to PayloadQuality.TEXT_LINES
            payload.text.isNotBlank() -> payload.text to PayloadQuality.VISIBLE_TEXT
            else -> return empty(payload, emptyBodies = maxOf(1, emptyMessagingBodies))
        }
        val body = selected.first.trim()
        if (body.isEmpty()) return empty(payload, emptyBodies = 1)
        val mapped = captured(
            payload = payload,
            observedAt = observedAt,
            conversation = conversation,
            sender = payload.title.ifBlank { "发送者未知" },
            body = body,
            quality = selected.second,
            identitySuffix = payload.postTime.toString(),
            messageAt = payload.postTime.takeIf { it > 0 }?.let(Instant::ofEpochMilli)
        )
        return MappedNotification(
            messages = listOf(mapped),
            quality = selected.second,
            titleLength = payload.title.length,
            bodyLength = body.length,
            sourceMessageCount = maxOf(1, payload.messages.size),
            emptyBodyCount = emptyMessagingBodies,
            payloadShape = payload.payloadShape
        )
    }

    private fun captured(
        payload: RawNotificationPayload,
        observedAt: Instant,
        conversation: String,
        sender: String,
        body: String,
        quality: PayloadQuality,
        identitySuffix: String,
        messageAt: Instant?
    ) = CapturedMessage(
        capturedAt = observedAt,
        messageAt = messageAt,
        conversation = conversation,
        sender = sender,
        visibleBody = body,
        quality = quality,
        sourcePackage = payload.sourcePackage,
        sourceIdentity = "${payload.sourceIdentity}|$identitySuffix",
        dingTalkVersion = payload.dingTalkVersion,
        conversationIsGroup = payload.isGroupConversation
    )

    private fun empty(payload: RawNotificationPayload, emptyBodies: Int = 0) = MappedNotification(
        messages = emptyList(),
        quality = null,
        titleLength = payload.title.length,
        bodyLength = 0,
        sourceMessageCount = payload.messages.size,
        emptyBodyCount = emptyBodies,
        payloadShape = payload.payloadShape
    )
}
