package io.github.zjay26.dinglater.data

import io.github.zjay26.dinglater.model.CapturedMessage
import io.github.zjay26.dinglater.model.InboxState
import io.github.zjay26.dinglater.model.PayloadQuality
import io.github.zjay26.dinglater.model.StoredMessage
import io.github.zjay26.dinglater.security.FingerprintService
import io.github.zjay26.dinglater.security.SecretBox
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import java.time.Duration
import java.time.Instant
import java.util.UUID

data class AppendResult(val message: StoredMessage?, val inserted: Boolean, val fingerprintPrefix: String)

class MessageRepository(
    private val dao: MessageDao,
    private val secretBox: SecretBox,
    private val fingerprints: FingerprintService
) {
    fun observeAll(): Flow<List<StoredMessage>> = dao.observeAll().map { entities -> entities.map(::decrypt) }

    suspend fun get(id: String): StoredMessage? = dao.get(id)?.let(::decrypt)

    suspend fun append(message: CapturedMessage, retentionDays: Int): AppendResult {
        val fingerprint = fingerprints.fingerprint(
            message.sourcePackage,
            message.sourceIdentity,
            message.messageAt?.toEpochMilli()?.toString().orEmpty(),
            message.conversation,
            message.sender,
            message.visibleBody
        )
        val now = message.capturedAt
        val stored = StoredMessage(
            id = UUID.randomUUID().toString(),
            captured = message,
            state = InboxState.INBOX,
            expiresAt = now.plus(Duration.ofDays(retentionDays.toLong())),
            snoozedUntil = null,
            updatedAt = now
        )
        val inserted = dao.insert(encrypt(stored, fingerprint)) != -1L
        return AppendResult(if (inserted) stored else null, inserted, fingerprint.take(12))
    }

    suspend fun snooze(id: String, dueAt: Instant, now: Instant = Instant.now()): StoredMessage {
        val existing = get(id) ?: throw NoSuchElementException("消息已不存在")
        require(dueAt.isAfter(now)) { "提醒时间必须晚于当前时间" }
        require(!dueAt.isAfter(existing.expiresAt)) { "提醒时间不能超过消息保留期限" }
        dao.updateState(id, InboxState.SNOOZED.name, dueAt.toEpochMilli(), now.toEpochMilli())
        return existing.copy(state = InboxState.SNOOZED, snoozedUntil = dueAt, updatedAt = now)
    }

    suspend fun markHandled(id: String, now: Instant = Instant.now()) =
        dao.updateState(id, InboxState.HANDLED.name, null, now.toEpochMilli())

    suspend fun restoreInbox(id: String, now: Instant = Instant.now()) =
        dao.updateState(id, InboxState.INBOX.name, null, now.toEpochMilli())

    suspend fun markAllHandled(state: InboxState, now: Instant = Instant.now()): Int {
        require(state == InboxState.INBOX || state == InboxState.SNOOZED)
        return dao.updateAllByState(state.name, InboxState.HANDLED.name, now.toEpochMilli())
    }

    suspend fun delete(id: String): Int = dao.delete(id)

    suspend fun deleteHandled(): Int = dao.deleteByState(InboxState.HANDLED.name)

    suspend fun deleteByState(state: InboxState): Int = dao.deleteByState(state.name)

    suspend fun deleteAll() = dao.deleteAll()

    suspend fun applyRetention(days: Int, now: Instant = Instant.now()) {
        dao.updateRetention(Duration.ofDays(days.coerceIn(1, 365).toLong()).toMillis())
        runMaintenance(now)
    }

    suspend fun runMaintenance(now: Instant = Instant.now()) {
        dao.deleteExpired(now.toEpochMilli())
        dao.releaseDue(now.toEpochMilli())
    }

    suspend fun listSnoozed(): List<StoredMessage> = dao.listSnoozed().map(::decrypt)

    suspend fun releaseDue(id: String, now: Instant = Instant.now()): StoredMessage? {
        val message = get(id) ?: return null
        if (message.state != InboxState.SNOOZED || message.snoozedUntil?.isAfter(now) != false) return null
        dao.updateState(id, InboxState.INBOX.name, null, now.toEpochMilli())
        return message.copy(state = InboxState.INBOX, snoozedUntil = null, updatedAt = now)
    }

    private fun encrypt(message: StoredMessage, fingerprint: String) = MessageEntity(
        id = message.id,
        fingerprint = fingerprint,
        capturedAt = message.captured.capturedAt.toEpochMilli(),
        messageAt = message.captured.messageAt?.toEpochMilli(),
        expiresAt = message.expiresAt.toEpochMilli(),
        updatedAt = message.updatedAt.toEpochMilli(),
        state = message.state.name,
        snoozedUntil = message.snoozedUntil?.toEpochMilli(),
        quality = message.captured.quality.name,
        sourcePackage = message.captured.sourcePackage,
        sourceIdentity = secretBox.encrypt(message.captured.sourceIdentity),
        dingTalkVersion = message.captured.dingTalkVersion,
        conversationIsGroup = message.captured.conversationIsGroup,
        conversation = secretBox.encrypt(message.captured.conversation),
        sender = secretBox.encrypt(message.captured.sender),
        body = secretBox.encrypt(message.captured.visibleBody)
    )

    private fun decrypt(entity: MessageEntity) = StoredMessage(
        id = entity.id,
        captured = CapturedMessage(
            capturedAt = Instant.ofEpochMilli(entity.capturedAt),
            messageAt = entity.messageAt?.let(Instant::ofEpochMilli),
            conversation = secretBox.decrypt(entity.conversation),
            sender = secretBox.decrypt(entity.sender),
            visibleBody = secretBox.decrypt(entity.body),
            quality = runCatching { PayloadQuality.valueOf(entity.quality) }.getOrDefault(PayloadQuality.VISIBLE_TEXT),
            sourcePackage = entity.sourcePackage,
            sourceIdentity = secretBox.decrypt(entity.sourceIdentity),
            dingTalkVersion = entity.dingTalkVersion,
            conversationIsGroup = entity.conversationIsGroup
        ),
        state = runCatching { InboxState.valueOf(entity.state) }.getOrDefault(InboxState.INBOX),
        expiresAt = Instant.ofEpochMilli(entity.expiresAt),
        snoozedUntil = entity.snoozedUntil?.let(Instant::ofEpochMilli),
        updatedAt = Instant.ofEpochMilli(entity.updatedAt)
    )
}
