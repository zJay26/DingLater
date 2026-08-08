package io.github.zjay26.dinglater.reminder

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import io.github.zjay26.dinglater.DingLaterApplication
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

class RestoreScheduleReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        val pending = goAsync()
        CoroutineScope(Dispatchers.IO).launch {
            try {
                val container = (context.applicationContext as DingLaterApplication).container
                container.repository.runMaintenance()
                container.reminders.reschedule(container.repository.listSnoozed())
            } finally {
                pending.finish()
            }
        }
    }
}
