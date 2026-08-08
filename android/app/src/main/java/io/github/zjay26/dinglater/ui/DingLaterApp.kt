package io.github.zjay26.dinglater.ui

import android.app.DatePickerDialog
import android.app.TimePickerDialog
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.github.zjay26.dinglater.MainUiState
import io.github.zjay26.dinglater.MainViewModel
import io.github.zjay26.dinglater.model.CaptureHealthState
import io.github.zjay26.dinglater.model.InboxState
import io.github.zjay26.dinglater.model.StoredMessage
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Calendar

private enum class AppSection(val label: String, val compact: String, val state: InboxState?) {
    INBOX("待处理", "待", InboxState.INBOX),
    SNOOZED("稍后提醒", "稍", InboxState.SNOOZED),
    HANDLED("已处理", "已", InboxState.HANDLED),
    SETTINGS("设置", "设", null)
}

private val DateTimeFormat = DateTimeFormatter.ofPattern("M月d日 HH:mm").withZone(ZoneId.systemDefault())
private val FullDateTimeFormat = DateTimeFormatter.ofPattern("yyyy年M月d日 HH:mm").withZone(ZoneId.systemDefault())

@Composable
fun DingLaterApp(
    viewModel: MainViewModel,
    requestedMessageId: String?,
    openNotificationAccess: () -> Unit,
    requestReminderPermission: () -> Unit
) {
    val uiState by viewModel.uiState.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    var section by rememberSaveable { mutableStateOf(AppSection.INBOX) }
    var selectedConversation by rememberSaveable { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) {
        viewModel.events.collect { snackbar.showSnackbar(it) }
    }
    LaunchedEffect(requestedMessageId, uiState.messages) {
        val requested = uiState.messages.firstOrNull { it.id == requestedMessageId } ?: return@LaunchedEffect
        section = AppSection.entries.first { it.state == requested.state }
        selectedConversation = requested.captured.conversation
    }

    if (!uiState.settings.onboardingCompleted) {
        OnboardingScreen(
            notificationAccessGranted = uiState.health.notificationAccessGranted,
            openNotificationAccess = openNotificationAccess,
            finish = {
                viewModel.completeOnboarding()
                requestReminderPermission()
            }
        )
        return
    }

    uiState.fatalError?.let { error ->
        AlertDialog(
            onDismissRequest = {},
            title = { Text("无法读取本地消息") },
            text = { Text(error) },
            confirmButton = { Button(onClick = viewModel::resetLocalData) { Text("删除并重新开始") } }
        )
    }

    Scaffold(
        snackbarHost = { SnackbarHost(snackbar) },
        bottomBar = {
            NavigationBar {
                AppSection.entries.forEach { item ->
                    val count = item.state?.let { state -> uiState.messages.count { it.state == state } }
                    NavigationBarItem(
                        selected = section == item,
                        onClick = {
                            section = item
                            selectedConversation = null
                        },
                        icon = {
                            Text(
                                item.compact,
                                fontWeight = FontWeight.Bold,
                                modifier = Modifier.semantics { contentDescription = item.label }
                            )
                        },
                        label = { Text(if (count != null && count > 0) "${item.label} $count" else item.label) }
                    )
                }
            }
        }
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.TopCenter) {
            when {
                section == AppSection.SETTINGS -> SettingsScreen(
                    uiState = uiState,
                    openNotificationAccess = openNotificationAccess,
                    requestReminderPermission = requestReminderPermission,
                    setCapturePaused = viewModel::setCapturePaused,
                    setReminderPreview = viewModel::setReminderPreview,
                    setRetentionDays = viewModel::setRetentionDays,
                    setQuickSnooze = viewModel::setQuickSnooze,
                    clearDiagnostics = viewModel::clearDiagnostics,
                    deleteAll = viewModel::deleteAll
                )
                selectedConversation != null -> ConversationScreen(
                    conversation = selectedConversation!!,
                    messages = uiState.messages.filter {
                        it.state == section.state && it.captured.conversation == selectedConversation
                    },
                    quickSnoozeMinutes = uiState.settings.quickSnoozeMinutes,
                    onBack = { selectedConversation = null },
                    onSnooze = viewModel::snooze,
                    onHandled = viewModel::markHandled,
                    onRestore = viewModel::restoreInbox,
                    onDelete = viewModel::delete
                )
                else -> InboxScreen(
                    section = section,
                    messages = uiState.messages.filter { it.state == section.state },
                    healthText = uiState.health.detail,
                    healthy = uiState.health.state == CaptureHealthState.HEALTHY,
                    openConversation = { selectedConversation = it },
                    markAllHandled = { section.state?.let(viewModel::markAllHandled) },
                    deleteAll = { section.state?.let(viewModel::deleteAllInState) }
                )
            }
        }
    }
}

@Composable
private fun OnboardingScreen(
    notificationAccessGranted: Boolean,
    openNotificationAccess: () -> Unit,
    finish: () -> Unit
) {
    Box(Modifier.fillMaxSize().safeDrawingPadding().padding(24.dp), contentAlignment = Alignment.Center) {
        Column(
            Modifier.widthIn(max = 560.dp).verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(22.dp)
        ) {
            Text("DingLater", style = MaterialTheme.typography.displaySmall, fontWeight = FontWeight.SemiBold)
            Text("把没空回复的消息，安静地放到稍后。", style = MaterialTheme.typography.titleLarge)
            OnboardingPoint("只在本机保存", "没有网络权限，正文使用设备密钥加密。")
            OnboardingPoint("只读取可见通知", "不会打开钉钉、操作通知或发送已读回执。")
            OnboardingPoint("内容可能不完整", "通知被隐藏或截断时，DingLater 会明确标记，不会猜测正文。")
            FilledTonalButton(onClick = openNotificationAccess, modifier = Modifier.fillMaxWidth()) {
                Text(if (notificationAccessGranted) "通知使用权已开启" else "开启通知使用权")
            }
            Button(
                onClick = finish,
                enabled = notificationAccessGranted,
                modifier = Modifier.fillMaxWidth()
            ) { Text("开始记录新通知") }
            Text(
                "只会记录完成此步骤后新出现的钉钉通知，不导入已有历史。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Composable
private fun OnboardingPoint(title: String, body: String) {
    Row(horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.Top) {
        Box(Modifier.padding(top = 5.dp).size(10.dp).clip(RoundedCornerShape(3.dp)).background(BookmarkPurple))
        Column {
            Text(title, fontWeight = FontWeight.SemiBold)
            Text(body, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun InboxScreen(
    section: AppSection,
    messages: List<StoredMessage>,
    healthText: String,
    healthy: Boolean,
    openConversation: (String) -> Unit,
    markAllHandled: () -> Unit,
    deleteAll: () -> Unit
) {
    var query by rememberSaveable(section) { mutableStateOf("") }
    var confirmDelete by rememberSaveable(section) { mutableStateOf(false) }
    val filtered = messages.filter { message ->
        query.isBlank() || listOf(
            message.captured.conversation,
            message.captured.sender,
            message.captured.visibleBody
        ).any { it.contains(query, ignoreCase = true) }
    }
    val conversations = filtered.groupBy { it.captured.conversation }
        .entries
        .sortedByDescending { it.value.maxOf { message -> messageTime(message) } }

    Column(Modifier.fillMaxHeight().widthIn(max = 760.dp).padding(horizontal = 16.dp)) {
        Row(
            Modifier.fillMaxWidth().padding(top = 18.dp, bottom = 10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(Modifier.weight(1f)) {
                Text(section.label, style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
                Text(healthText, style = MaterialTheme.typography.bodySmall, color = if (healthy) CalmGreen else ReminderAmber)
            }
            if (messages.isNotEmpty()) {
                Row {
                    if (section != AppSection.HANDLED) {
                        TextButton(onClick = markAllHandled) { Text("全部处理") }
                    }
                    TextButton(onClick = { confirmDelete = true }) { Text("全部删除") }
                }
            }
        }
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            label = { Text("搜索会话、发送者或正文") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth()
        )
        Spacer(Modifier.height(12.dp))
        if (conversations.isEmpty()) {
            EmptyState(section, Modifier.fillMaxSize())
        } else {
            LazyColumn(contentPadding = PaddingValues(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                items(conversations, key = { it.key }) { entry ->
                    ConversationCard(section.state!!, entry.key, entry.value, openConversation)
                }
            }
        }
    }
    if (confirmDelete) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("删除“${section.label}”中的全部消息？") },
            text = { Text("本地正文、状态和相关提醒将永久删除，不影响钉钉。") },
            confirmButton = {
                Button(onClick = { confirmDelete = false; deleteAll() }) { Text("全部删除") }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("取消") } }
        )
    }
}

@Composable
private fun ConversationCard(
    state: InboxState,
    conversation: String,
    messages: List<StoredMessage>,
    open: (String) -> Unit
) {
    val latest = messages.maxBy { messageTime(it) }
    val accent = stateColor(state)
    Card(
        modifier = Modifier.fillMaxWidth().clickable { open(conversation) },
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)
    ) {
        Row(Modifier.fillMaxWidth()) {
            Box(Modifier.width(7.dp).height(112.dp).background(accent))
            Column(Modifier.padding(16.dp).weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(conversation, modifier = Modifier.weight(1f), fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    if (messages.size > 1) Text("${messages.size} 条", style = MaterialTheme.typography.labelMedium, color = accent)
                }
                Text(latest.captured.sender, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text(latest.captured.visibleBody, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Row {
                    Text(DateTimeFormat.format(latest.captured.messageAt ?: latest.captured.capturedAt), style = MaterialTheme.typography.labelSmall)
                    latest.snoozedUntil?.let { Text("  ·  提醒 ${DateTimeFormat.format(it)}", style = MaterialTheme.typography.labelSmall, color = ReminderAmber) }
                }
            }
        }
    }
}

@Composable
private fun EmptyState(section: AppSection, modifier: Modifier = Modifier) {
    val (title, detail) = when (section) {
        AppSection.SNOOZED -> "没有稍后提醒" to "需要稍后处理的消息会显示在这里。"
        AppSection.HANDLED -> "还没有已处理消息" to "处理过的消息会保留到清理时间。"
        else -> "待处理已清空" to "新的钉钉通知会按会话归拢到这里。"
    }
    Box(modifier.padding(24.dp), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Medium)
            Spacer(Modifier.height(8.dp))
            Text(detail, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun ConversationScreen(
    conversation: String,
    messages: List<StoredMessage>,
    quickSnoozeMinutes: Int,
    onBack: () -> Unit,
    onSnooze: (String, Instant) -> Unit,
    onHandled: (String) -> Unit,
    onRestore: (String) -> Unit,
    onDelete: (String) -> Unit
) {
    BackHandler(onBack = onBack)
    Column(Modifier.fillMaxHeight().widthIn(max = 760.dp).padding(horizontal = 16.dp)) {
        Row(Modifier.fillMaxWidth().padding(vertical = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onBack) { Text("返回") }
            Text(conversation, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        LazyColumn(contentPadding = PaddingValues(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            items(messages.sortedByDescending { messageTime(it) }, key = { it.id }) { message ->
                MessageCard(message, quickSnoozeMinutes, onSnooze, onHandled, onRestore, onDelete)
            }
        }
    }
}

@Composable
private fun MessageCard(
    message: StoredMessage,
    quickSnoozeMinutes: Int,
    onSnooze: (String, Instant) -> Unit,
    onHandled: (String) -> Unit,
    onRestore: (String) -> Unit,
    onDelete: (String) -> Unit
) {
    var showSnooze by remember { mutableStateOf(false) }
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row {
                Text(message.captured.sender, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
                Text(DateTimeFormat.format(message.captured.messageAt ?: message.captured.capturedAt), style = MaterialTheme.typography.labelMedium)
            }
            Text(message.captured.visibleBody, style = MaterialTheme.typography.bodyLarge)
            if (message.captured.quality.potentiallyIncomplete) {
                Text("内容可能不完整 · ${message.captured.quality.label}", color = ReminderAmber, style = MaterialTheme.typography.labelMedium)
            }
            message.snoozedUntil?.let {
                Text("提醒时间：${FullDateTimeFormat.format(it)}", color = ReminderAmber, fontWeight = FontWeight.Medium)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                when (message.state) {
                    InboxState.INBOX -> {
                        TextButton(onClick = { showSnooze = true }) { Text("稍后") }
                        TextButton(onClick = { onHandled(message.id) }) { Text("已处理") }
                    }
                    InboxState.SNOOZED -> {
                        TextButton(onClick = { onRestore(message.id) }) { Text("回到待处理") }
                        TextButton(onClick = { onHandled(message.id) }) { Text("已处理") }
                    }
                    InboxState.HANDLED -> TextButton(onClick = { onRestore(message.id) }) { Text("回到待处理") }
                }
                Spacer(Modifier.weight(1f))
                TextButton(onClick = { onDelete(message.id) }) { Text("删除", color = MaterialTheme.colorScheme.error) }
            }
        }
    }
    if (showSnooze) {
        SnoozeDialog(
            quickMinutes = quickSnoozeMinutes,
            expiresAt = message.expiresAt,
            dismiss = { showSnooze = false },
            select = {
                showSnooze = false
                onSnooze(message.id, it)
            }
        )
    }
}

@Composable
private fun SnoozeDialog(quickMinutes: Int, expiresAt: Instant, dismiss: () -> Unit, select: (Instant) -> Unit) {
    val context = LocalContext.current
    val now = Instant.now()
    fun pickCustom() {
        val calendar = Calendar.getInstance()
        DatePickerDialog(context, { _, year, month, day ->
            TimePickerDialog(context, { _, hour, minute ->
                val selected = Calendar.getInstance().apply {
                    set(year, month, day, hour, minute, 0)
                    set(Calendar.MILLISECOND, 0)
                }.toInstant()
                if (selected.isAfter(now) && !selected.isAfter(expiresAt)) select(selected)
            }, calendar.get(Calendar.HOUR_OF_DAY), calendar.get(Calendar.MINUTE), true).show()
        }, calendar.get(Calendar.YEAR), calendar.get(Calendar.MONTH), calendar.get(Calendar.DAY_OF_MONTH)).show()
    }
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text("稍后提醒") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                FilledTonalButton(onClick = { select(now.plusSeconds(quickMinutes * 60L)) }, modifier = Modifier.fillMaxWidth()) { Text("$quickMinutes 分钟后") }
                FilledTonalButton(onClick = { select(now.plusSeconds(60 * 60L)) }, modifier = Modifier.fillMaxWidth()) { Text("1 小时后") }
                OutlinedButton(onClick = ::pickCustom, modifier = Modifier.fillMaxWidth()) { Text("选择日期和时间") }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = dismiss) { Text("取消") } }
    )
}

@Composable
private fun SettingsScreen(
    uiState: MainUiState,
    openNotificationAccess: () -> Unit,
    requestReminderPermission: () -> Unit,
    setCapturePaused: (Boolean) -> Unit,
    setReminderPreview: (Boolean) -> Unit,
    setRetentionDays: (Int) -> Unit,
    setQuickSnooze: (Int) -> Unit,
    clearDiagnostics: () -> Unit,
    deleteAll: () -> Unit
) {
    var retentionText by remember(uiState.settings.retentionDays) { mutableStateOf(uiState.settings.retentionDays.toString()) }
    var quickText by remember(uiState.settings.quickSnoozeMinutes) { mutableStateOf(uiState.settings.quickSnoozeMinutes.toString()) }
    var confirmDelete by remember { mutableStateOf(false) }
    LazyColumn(
        modifier = Modifier.fillMaxHeight().widthIn(max = 760.dp).padding(horizontal = 16.dp),
        contentPadding = PaddingValues(top = 18.dp, bottom = 30.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        item { Text("设置", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold) }
        item {
            SettingsCard("消息捕获", "实际范围由钉钉通知设置决定") {
                StatusLine("通知使用权", uiState.health.notificationAccessGranted)
                StatusLine("钉钉已安装", uiState.health.dingTalkDetected)
                StatusLine("通知服务已连接", uiState.health.listenerConnected)
                OutlinedButton(onClick = openNotificationAccess) { Text("管理通知使用权") }
                SettingSwitch("暂停捕获", uiState.settings.capturePaused, setCapturePaused)
            }
        }
        item {
            SettingsCard("稍后提醒", "省电模式可能延迟提醒，但不会提前") {
                StatusLine("允许 DingLater 发通知", uiState.health.reminderPermissionGranted)
                if (!uiState.health.reminderPermissionGranted) OutlinedButton(onClick = requestReminderPermission) { Text("允许提醒通知") }
                SettingSwitch("通知显示正文预览", uiState.settings.showReminderPreview, setReminderPreview)
                NumberSetting("快速稍后（分钟）", quickText, { quickText = it }) {
                    quickText.toIntOrNull()?.let(setQuickSnooze)
                }
            }
        }
        item {
            SettingsCard("本地保留", "到期后永久删除，不影响钉钉") {
                NumberSetting("保留天数（1–365）", retentionText, { retentionText = it }) {
                    retentionText.toIntOrNull()?.let(setRetentionDays)
                }
                OutlinedButton(onClick = { confirmDelete = true }) { Text("删除全部本地消息") }
            }
        }
        item {
            val diagnostics = uiState.diagnostics
            SettingsCard("捕获诊断", "只记录字段类型、长度、计数和 HMAC，不含正文") {
                Text("观察 ${diagnostics.observationCount} · 保存 ${diagnostics.capturedCount} · 去重 ${diagnostics.duplicateCount} · 空正文 ${diagnostics.emptyBodyCount}")
                Text("单条失败 ${diagnostics.failureCount} 次（失败不会停止后续捕获）")
                Text("最近字段：标题 ${diagnostics.lastTitleLength} 字，正文 ${diagnostics.lastBodyLength} 字，消息 ${diagnostics.lastMessageCount} 条")
                Text("字段形态：${diagnostics.lastPayloadShape.ifBlank { "—" }}")
                Text("最近来源：${diagnostics.lastQuality?.label ?: "尚无"} · 指纹 ${diagnostics.lastFingerprintPrefix.ifBlank { "—" }}")
                diagnostics.lastObservationAt?.let { Text("最近观察：${FullDateTimeFormat.format(it)}") }
                diagnostics.lastFailureAt?.let {
                    Text("最近失败：${diagnostics.lastFailureKind.ifBlank { "CaptureException" }} · ${FullDateTimeFormat.format(it)}")
                }
                TextButton(onClick = clearDiagnostics) { Text("清空诊断") }
            }
        }
        item {
            Text(
                "DingLater Android 0.1.0 · 独立通知版\n不读取钉钉私有文件，不打开钉钉，不连接网络。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
    if (confirmDelete) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("删除全部本地消息？") },
            text = { Text("消息正文、处理状态和稍后提醒将永久删除。钉钉中的消息不会受影响。") },
            confirmButton = {
                Button(onClick = { confirmDelete = false; deleteAll() }) { Text("全部删除") }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("取消") } }
        )
    }
}

@Composable
private fun SettingsCard(title: String, description: String, content: @Composable ColumnScope.() -> Unit) {
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            Text(description, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            HorizontalDivider()
            content()
        }
    }
}

@Composable
private fun StatusLine(label: String, enabled: Boolean) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f))
        Text(if (enabled) "已开启" else "未开启", color = if (enabled) CalmGreen else ReminderAmber, fontWeight = FontWeight.Medium)
    }
}

@Composable
private fun SettingSwitch(label: String, checked: Boolean, onCheckedChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f))
        Switch(checked = checked, onCheckedChange = onCheckedChange)
    }
}

@Composable
private fun NumberSetting(label: String, value: String, setValue: (String) -> Unit, save: () -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedTextField(
            value = value,
            onValueChange = { setValue(it.filter(Char::isDigit).take(4)) },
            label = { Text(label) },
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
            singleLine = true,
            modifier = Modifier.weight(1f)
        )
        Button(onClick = save, enabled = value.isNotBlank()) { Text("保存") }
    }
}

private fun messageTime(message: StoredMessage): Long = (message.captured.messageAt ?: message.captured.capturedAt).toEpochMilli()

@Composable
private fun stateColor(state: InboxState): Color = when (state) {
    InboxState.INBOX -> BookmarkPurple
    InboxState.SNOOZED -> ReminderAmber
    InboxState.HANDLED -> HandledSlate
}
