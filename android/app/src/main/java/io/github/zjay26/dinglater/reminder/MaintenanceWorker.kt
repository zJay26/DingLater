package io.github.zjay26.dinglater.reminder

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import io.github.zjay26.dinglater.DingLaterApplication

class MaintenanceWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result = try {
        val container = (applicationContext as DingLaterApplication).container
        container.repository.runMaintenance()
        container.reminders.reschedule(container.repository.listSnoozed())
        Result.success()
    } catch (_: Exception) {
        Result.retry()
    }

    companion object {
        const val WORK_NAME = "dinglater_daily_maintenance"
    }
}
