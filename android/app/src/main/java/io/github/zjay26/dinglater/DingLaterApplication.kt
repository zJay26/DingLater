package io.github.zjay26.dinglater

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import androidx.room.Room
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import io.github.zjay26.dinglater.data.AppPreferences
import io.github.zjay26.dinglater.data.DingLaterDatabase
import io.github.zjay26.dinglater.data.MessageRepository
import io.github.zjay26.dinglater.reminder.MaintenanceWorker
import io.github.zjay26.dinglater.reminder.ReminderScheduler
import io.github.zjay26.dinglater.security.FingerprintService
import io.github.zjay26.dinglater.security.SecretBox
import java.util.concurrent.TimeUnit

class DingLaterApplication : Application() {
    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()
        val database = Room.databaseBuilder(this, DingLaterDatabase::class.java, "dinglater.db")
            .build()
        val preferences = AppPreferences(this)
        val secretBox = SecretBox()
        val fingerprints = FingerprintService()
        val repository = MessageRepository(database.messages(), secretBox, fingerprints)
        container = AppContainer(
            preferences = preferences,
            repository = repository,
            reminders = ReminderScheduler(this),
            secretBox = secretBox,
            fingerprints = fingerprints
        )
        createReminderChannel()
        WorkManager.getInstance(this).enqueueUniquePeriodicWork(
            MaintenanceWorker.WORK_NAME,
            ExistingPeriodicWorkPolicy.KEEP,
            PeriodicWorkRequestBuilder<MaintenanceWorker>(24, TimeUnit.HOURS).build()
        )
    }

    private fun createReminderChannel() {
        val channel = NotificationChannel(
            REMINDER_CHANNEL_ID,
            getString(R.string.reminder_channel_name),
            NotificationManager.IMPORTANCE_DEFAULT
        ).apply { description = getString(R.string.reminder_channel_description) }
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    companion object {
        const val REMINDER_CHANNEL_ID = "dinglater_reminders"
    }
}

data class AppContainer(
    val preferences: AppPreferences,
    val repository: MessageRepository,
    val reminders: ReminderScheduler,
    val secretBox: SecretBox,
    val fingerprints: FingerprintService
)
