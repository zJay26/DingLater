package io.github.zjay26.dinglater.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

val BookmarkPurple = Color(0xFF6552A3)
val ReminderAmber = Color(0xFFB66A14)
val HandledSlate = Color(0xFF68717E)
val CalmGreen = Color(0xFF2D765A)

private val LightColors = lightColorScheme(
    primary = BookmarkPurple,
    onPrimary = Color.White,
    primaryContainer = Color(0xFFE9E1FF),
    onPrimaryContainer = Color(0xFF21154C),
    secondary = Color(0xFF76557D),
    tertiary = ReminderAmber,
    background = Color(0xFFF8F7FC),
    surface = Color(0xFFFFFBFF),
    surfaceVariant = Color(0xFFE8E5EC),
    outline = Color(0xFF77727D)
)

private val DarkColors = darkColorScheme(
    primary = Color(0xFFCFBDFF),
    onPrimary = Color(0xFF362568),
    primaryContainer = Color(0xFF4D3C84),
    onPrimaryContainer = Color(0xFFE9E1FF),
    secondary = Color(0xFFE4B8EA),
    tertiary = Color(0xFFFFB86B),
    background = Color(0xFF111016),
    surface = Color(0xFF19181F),
    surfaceVariant = Color(0xFF49464E),
    outline = Color(0xFF918D98)
)

@Composable
fun DingLaterTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = if (isSystemInDarkTheme()) DarkColors else LightColors,
        content = content
    )
}
