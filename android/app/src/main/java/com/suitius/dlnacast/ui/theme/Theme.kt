package com.suitius.dlnacast.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val LightColors = lightColorScheme(
    primary = Color(0xFF3E51AB),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFDFE3FF),
    onPrimaryContainer = Color(0xFF172665),
    secondary = Color(0xFF555D7E),
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFE7EAFA),
    onSecondaryContainer = Color(0xFF202746),
    tertiary = Color(0xFF426364),
    onTertiary = Color.White,
    tertiaryContainer = Color(0xFFC4E8E9),
    onTertiaryContainer = Color(0xFF102F30),
    background = Color(0xFFFBF8FF),
    onBackground = Color(0xFF1B1B23),
    surface = Color(0xFFFBF8FF),
    onSurface = Color(0xFF1B1B23),
    surfaceVariant = Color(0xFFE3E1EE),
    onSurfaceVariant = Color(0xFF464652),
    outline = Color(0xFF777683),
    outlineVariant = Color(0xFFC7C5D3),
    surfaceContainerLowest = Color.White,
    surfaceContainerLow = Color(0xFFF3F1FA),
    surfaceContainer = Color(0xFFEFEDF6),
    surfaceContainerHigh = Color(0xFFE9E7F1),
    surfaceContainerHighest = Color(0xFFE3E1EB),
)

private val DarkColors = darkColorScheme(
    primary = Color(0xFFBBC4FF),
    onPrimary = Color(0xFF202F73),
    primaryContainer = Color(0xFF333F8D),
    onPrimaryContainer = Color(0xFFDFE3FF),
    secondary = Color(0xFFBEC5E8),
    onSecondary = Color(0xFF292F4B),
    secondaryContainer = Color(0xFF414664),
    onSecondaryContainer = Color(0xFFE7EAFA),
    tertiary = Color(0xFFA8CDCE),
    onTertiary = Color(0xFF193637),
    tertiaryContainer = Color(0xFF2A4C4D),
    onTertiaryContainer = Color(0xFFC4E8E9),
    background = Color(0xFF13131B),
    onBackground = Color(0xFFE6E3EE),
    surface = Color(0xFF13131B),
    onSurface = Color(0xFFE6E3EE),
    surfaceVariant = Color(0xFF464652),
    onSurfaceVariant = Color(0xFFC7C5D3),
    outline = Color(0xFF92909F),
    outlineVariant = Color(0xFF464652),
    surfaceContainerLowest = Color(0xFF0E0E15),
    surfaceContainerLow = Color(0xFF1C1C25),
    surfaceContainer = Color(0xFF20202A),
    surfaceContainerHigh = Color(0xFF2A2933),
    surfaceContainerHighest = Color(0xFF35343E),
)

@Composable
fun DLNACastTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = if (isSystemInDarkTheme()) DarkColors else LightColors,
        content = content,
    )
}
