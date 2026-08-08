package io.github.zjay26.dinglater.reminder

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import io.github.zjay26.dinglater.model.StoredMessage
import java.time.Instant

class ReminderScheduler(private val context: Context) {
    private val alarms = context.getSystemService(AlarmManager::class.java)

    fun schedule(messageId: String, dueAt: Instant) {
        alarms.setAndAllowWhileIdle(
            AlarmManager.RTC_WAKEUP,
            dueAt.toEpochMilli(),
            pendingIntent(messageId)
        )
    }

    fun cancel(messageId: String) = alarms.cancel(pendingIntent(messageId))

    fun reschedule(messages: List<StoredMessage>) {
        remindersToRestore(messages, Instant.now()).forEach { (messageId, dueAt) -> schedule(messageId, dueAt) }
    }

    private fun pendingIntent(messageId: String): PendingIntent {
        val intent = Intent(context, ReminderReceiver::class.java)
            .setAction(ACTION_REMIND)
            .putExtra(EXTRA_MESSAGE_ID, messageId)
        return PendingIntent.getBroadcast(
            context,
            messageId.hashCode(),
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }

    companion object {
        const val ACTION_REMIND = "io.github.zjay26.dinglater.REMIND"
        const val EXTRA_MESSAGE_ID = "message_id"
    }
}
