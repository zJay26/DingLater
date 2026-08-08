package io.github.zjay26.dinglater

import android.Manifest
import android.app.Application
import android.app.NotificationManager
import android.content.ComponentName
import android.content.pm.PackageManager
import android.os.Build
import android.service.notification.NotificationListenerService
import androidx.core.content.ContextCompat
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import io.github.zjay26.dinglater.capture.DingTalkNotificationListener
import io.github.zjay26.dinglater.model.AppSettings
import io.github.zjay26.dinglater.model.CaptureDiagnostics
import io.github.zjay26.dinglater.model.CaptureHealth
import io.github.zjay26.dinglater.model.CaptureHealthState
import io.github.zjay26.dinglater.model.InboxState
import io.github.zjay26.dinglater.model.StoredMessage
import io.github.zjay26.dinglater.security.LocalKeyUnavailableException
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import java.time.Instant

data class SystemState(
    val notificationAccess: Boolean = false,
    val reminderPermission: Boolean = false,
    val dingTalkDetected: Boolean = false
)

data class MainUiState(
    val messages: List<StoredMessage> = emptyList(),
    val settings: AppSettings = AppSettings(),
    val diagnostics: CaptureDiagnostics = CaptureDiagnostics(),
    val health: CaptureHealth = CaptureHealth(
        CaptureHealthState.STOPPED,
        "正在准备",
        false,
        false,
        false,
        false
    ),
    val fatalError: String? = null
)

@OptIn(ExperimentalCoroutinesApi::class)
class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val container = (application as DingLaterApplication).container
    private val systemState = MutableStateFlow(SystemState())
    private val fatalError = MutableStateFlow<String?>(null)
    private val messageGeneration = MutableStateFlow(0)
    private val messages = messageGeneration.flatMapLatest {
        container.repository.observeAll().catch { throwable ->
            handleFailure(throwable)
            emit(emptyList())
        }
    }
    private val eventsChannel = Channel<String>(Channel.BUFFERED)
    val events = eventsChannel.receiveAsFlow()

    val uiState = combine(
        messages,
        container.preferences.settings,
        container.preferences.diagnostics,
        systemState,
        fatalError
    ) { allMessages, settings, diagnostics, system, fatal ->
        MainUiState(
            messages = allMessages,
            settings = settings,
            diagnostics = diagnostics,
            health = captureHealth(settings, system),
            fatalError = fatal
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), MainUiState())

    init {
        refreshSystemState()
        viewModelScope.launch {
            runCatching {
                container.repository.runMaintenance()
                container.reminders.reschedule(container.repository.listSnoozed())
            }.onFailure(::handleFailure)
        }
    }

    fun refreshSystemState() {
        val app = getApplication<Application>()
        val notificationManager = app.getSystemService(NotificationManager::class.java)
        val listener = ComponentName(app, DingTalkNotificationListener::class.java)
        val access = notificationManager.isNotificationListenerAccessGranted(listener)
        val reminder = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
            ContextCompat.checkSelfPermission(app, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        val dingTalk = try {
            app.packageManager.getApplicationInfo(DingTalkNotificationListener.DINGTALK_PACKAGE, 0)
            true
        } catch (_: PackageManager.NameNotFoundException) {
            false
        }
        systemState.value = SystemState(access, reminder, dingTalk)
        if (access) NotificationListenerService.requestRebind(listener)
    }

    fun completeOnboarding() = launchAction { container.preferences.completeOnboarding(Instant.now()) }

    fun setCapturePaused(paused: Boolean) = launchAction { container.preferences.setCapturePaused(paused) }

    fun setReminderPreview(enabled: Boolean) = launchAction { container.preferences.setReminderPreview(enabled) }

    fun setQuickSnooze(minutes: Int) = launchAction { container.preferences.setQuickSnoozeMinutes(minutes) }

    fun setRetentionDays(days: Int) = launchAction {
        val normalized = days.coerceIn(1, 365)
        container.preferences.setRetentionDays(normalized)
        container.repository.applyRetention(normalized)
    }

    fun snooze(id: String, dueAt: Instant) = launchAction {
        val message = container.repository.snooze(id, dueAt)
        container.reminders.schedule(message.id, dueAt)
    }

    fun markHandled(id: String) = launchAction {
        container.reminders.cancel(id)
        container.repository.markHandled(id)
    }

    fun restoreInbox(id: String) = launchAction {
        container.reminders.cancel(id)
        container.repository.restoreInbox(id)
    }

    fun delete(id: String) = launchAction {
        container.reminders.cancel(id)
        container.repository.delete(id)
    }

    fun markAllHandled(state: InboxState) = launchAction {
        if (state == InboxState.SNOOZED) {
            container.repository.listSnoozed().forEach { container.reminders.cancel(it.id) }
        }
        container.repository.markAllHandled(state)
    }

    fun deleteHandled() = launchAction { container.repository.deleteHandled() }

    fun deleteAllInState(state: InboxState) = launchAction {
        if (state == InboxState.SNOOZED) {
            container.repository.listSnoozed().forEach { container.reminders.cancel(it.id) }
        }
        container.repository.deleteByState(state)
    }

    fun deleteAll() = launchAction {
        container.repository.listSnoozed().forEach { container.reminders.cancel(it.id) }
        container.repository.deleteAll()
    }

    fun clearDiagnostics() = launchAction { container.preferences.clearDiagnostics() }

    fun resetLocalData() = launchAction {
        container.repository.deleteAll()
        container.secretBox.reset()
        container.fingerprints.reset()
        container.preferences.resetAll()
        messageGeneration.value += 1
        fatalError.value = null
        eventsChannel.trySend("本地数据和密钥已重置")
    }

    private fun launchAction(action: suspend () -> Unit) {
        viewModelScope.launch {
            runCatching { action() }.onFailure(::handleFailure)
        }
    }

    private fun handleFailure(throwable: Throwable) {
        if (throwable is LocalKeyUnavailableException || throwable.cause is LocalKeyUnavailableException) {
            fatalError.value = "本地加密密钥不可用。请删除本地数据后重新开始。"
        } else {
            eventsChannel.trySend(throwable.message ?: "操作未完成")
        }
    }

    private fun captureHealth(settings: AppSettings, system: SystemState): CaptureHealth {
        val state = when {
            settings.captureFaulted -> CaptureHealthState.FAULTED
            settings.capturePaused -> CaptureHealthState.PAUSED
            !system.notificationAccess -> CaptureHealthState.PERMISSION_DENIED
            !system.dingTalkDetected -> CaptureHealthState.UNAVAILABLE
            settings.listenerConnected -> CaptureHealthState.HEALTHY
            else -> CaptureHealthState.STOPPED
        }
        val detail = when (state) {
            CaptureHealthState.PAUSED -> "捕获已暂停"
            CaptureHealthState.PERMISSION_DENIED -> "需要通知使用权"
            CaptureHealthState.UNAVAILABLE -> "没有检测到钉钉"
            CaptureHealthState.HEALTHY -> "正在被动接收新通知"
            CaptureHealthState.FAULTED -> "采集已停止，请重置本地数据"
            else -> "等待系统连接通知服务"
        }
        return CaptureHealth(
            state = state,
            detail = detail,
            notificationAccessGranted = system.notificationAccess,
            reminderPermissionGranted = system.reminderPermission,
            dingTalkDetected = system.dingTalkDetected,
            listenerConnected = settings.listenerConnected
        )
    }

    class Factory(private val application: Application) : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T = MainViewModel(application) as T
    }
}
