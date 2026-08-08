package io.github.zjay26.dinglater

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat
import io.github.zjay26.dinglater.reminder.ReminderScheduler
import io.github.zjay26.dinglater.ui.DingLaterApp
import io.github.zjay26.dinglater.ui.DingLaterTheme

class MainActivity : ComponentActivity() {
    private val viewModel by viewModels<MainViewModel> { MainViewModel.Factory(application) }
    private var requestedMessageId by mutableStateOf<String?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        requestedMessageId = intent.getStringExtra(ReminderScheduler.EXTRA_MESSAGE_ID)
        setContent {
            DingLaterTheme {
                val context = LocalContext.current
                val notificationPermission = rememberLauncherForActivityResult(
                    ActivityResultContracts.RequestPermission()
                ) { viewModel.refreshSystemState() }
                DingLaterApp(
                    viewModel = viewModel,
                    requestedMessageId = requestedMessageId,
                    openNotificationAccess = {
                        context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
                    },
                    requestReminderPermission = {
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
                            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
                        ) {
                            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                        }
                    }
                )
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        requestedMessageId = intent.getStringExtra(ReminderScheduler.EXTRA_MESSAGE_ID)
    }

    override fun onResume() {
        super.onResume()
        viewModel.refreshSystemState()
    }
}
