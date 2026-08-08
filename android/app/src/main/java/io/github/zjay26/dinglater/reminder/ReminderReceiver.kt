package io.github.zjay26.dinglater.reminder

import android.Manifest
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import io.github.zjay26.dinglater.DingLaterApplication
import io.github.zjay26.dinglater.MainActivity
import io.github.zjay26.dinglater.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

class ReminderReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val messageId = intent.getStringExtra(ReminderScheduler.EXTRA_MESSAGE_ID) ?: return
        val pending = goAsync()
        CoroutineScope(Dispatchers.IO).launch {
            try {
                val app = context.applicationContext as DingLaterApplication
                val message = app.container.repository.releaseDue(messageId) ?: return@launch
                val settings = app.container.preferences.currentSettings()
                if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
                    ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
                ) {
                    val openApp = PendingIntent.getActivity(
                        context,
                        message.id.hashCode(),
                        Intent(context, MainActivity::class.java)
                            .putExtra(ReminderScheduler.EXTRA_MESSAGE_ID, message.id)
                            .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP),
                        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
                    )
                    val notification = NotificationCompat.Builder(context, DingLaterApplication.REMINDER_CHANNEL_ID)
                        .setSmallIcon(R.drawable.ic_notification)
                        .setContentTitle("该回复了 · ${message.captured.conversation}")
                        .setContentText(if (settings.showReminderPreview) message.captured.visibleBody else "打开 DingLater 查看")
                        .setContentIntent(openApp)
                        .setAutoCancel(true)
                        .setCategory(NotificationCompat.CATEGORY_REMINDER)
                        .apply {
                            if (settings.showReminderPreview) {
                                setStyle(NotificationCompat.BigTextStyle().bigText(message.captured.visibleBody))
                            }
                        }
                        .build()
                    NotificationManagerCompat.from(context).notify(message.id.hashCode(), notification)
                }
            } finally {
                pending.finish()
            }
        }
    }
}
