package io.github.zjay26.dinglater.reminder

import io.github.zjay26.dinglater.model.CapturedMessage
import io.github.zjay26.dinglater.model.InboxState
import io.github.zjay26.dinglater.model.PayloadQuality
import io.github.zjay26.dinglater.model.StoredMessage
import org.junit.Assert.assertEquals
import org.junit.Test
import java.time.Instant

class ReminderPolicyTest {
    private val now = Instant.parse("2026-08-06T04:00:00Z")

    @Test
    fun `restore includes only future snoozed reminders`() {
        val future = message("future", InboxState.SNOOZED, now.plusSeconds(60))
        val due = message("due", InboxState.SNOOZED, now)
        val inbox = message("inbox", InboxState.INBOX, now.plusSeconds(120))

        assertEquals(listOf("future" to now.plusSeconds(60)), remindersToRestore(listOf(future, due, inbox), now))
    }

    private fun message(id: String, state: InboxState, dueAt: Instant?) = StoredMessage(
        id = id,
        captured = CapturedMessage(
            capturedAt = now.minusSeconds(30),
            messageAt = now.minusSeconds(30),
            conversation = "会话",
            sender = "发送者",
            visibleBody = "正文",
            quality = PayloadQuality.BIG_TEXT,
            sourcePackage = "com.alibaba.android.rimet",
            sourceIdentity = id,
            dingTalkVersion = "test",
            conversationIsGroup = false
        ),
        state = state,
        expiresAt = now.plusSeconds(3600),
        snoozedUntil = dueAt,
        updatedAt = now
    )
}
