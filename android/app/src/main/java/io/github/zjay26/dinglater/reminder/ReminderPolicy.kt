package io.github.zjay26.dinglater.reminder

import io.github.zjay26.dinglater.model.InboxState
import io.github.zjay26.dinglater.model.StoredMessage
import java.time.Instant

internal fun remindersToRestore(messages: List<StoredMessage>, now: Instant): List<Pair<String, Instant>> =
    messages.mapNotNull { message ->
        val dueAt = message.snoozedUntil
        if (message.state == InboxState.SNOOZED && dueAt != null && dueAt.isAfter(now)) message.id to dueAt else null
    }
