package com.suitius.dlnacast.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.suitius.dlnacast.R
import com.suitius.dlnacast.audio.AudioChannelRoute
import com.suitius.dlnacast.audio.CaptureSource
import com.suitius.dlnacast.model.SpeakerTarget
import com.suitius.dlnacast.service.CastProtocol
import com.suitius.dlnacast.service.CastStatus
import kotlin.math.roundToInt

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun CastHome(
    status: CastStatus,
    protocol: CastProtocol,
    targets: List<SpeakerTarget>,
    selectedTargetIds: List<String>,
    scanning: Boolean,
    discoveryError: String?,
    stereoSplit: Boolean,
    onOpenSettings: () -> Unit,
    onRefresh: () -> Unit,
    onToggleTarget: (SpeakerTarget) -> Unit,
    onStart: () -> Unit,
    onStop: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit,
    onSetVolume: (Int, Int?) -> Unit,
    onSetGroupVolume: (Int, List<String>) -> Unit,
) {
    val streaming = status as? CastStatus.Streaming
    val preparing = status is CastStatus.Preparing
    val selectedTargets = targets.filter { it.id in selectedTargetIds }
    val selectedCount = selectedTargets.size
    val split = stereoSplit && protocol == CastProtocol.MIPLAY
    val validSelection = selectedCount > 0 && selectedTargets.all { it.isAvailable } &&
        (!split || selectedCount == 2)

    Scaffold(
        contentWindowInsets = WindowInsets.safeDrawing,
        topBar = {
            TopAppBar(
                title = { Text("MiPlay Cast") },
                actions = {
                    IconButton(onClick = onOpenSettings) {
                        Icon(Icons.Default.Settings, contentDescription = "设置")
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.surface,
                ),
            )
        },
        bottomBar = {
            CastActionBar(
                status = status,
                canStart = validSelection,
                onStart = onStart,
                onStop = onStop,
                onPause = onPause,
                onResume = onResume,
            )
        },
    ) { padding ->
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
            LazyColumn(
                modifier = Modifier
                    .widthIn(max = 640.dp)
                    .fillMaxSize()
                    .padding(padding)
                    .consumeWindowInsets(padding),
                contentPadding = PaddingValues(start = 20.dp, top = 12.dp, end = 20.dp, bottom = 20.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                item { SessionCard(status, protocol) }
                if (streaming != null && streaming.receiverControls.size > 1 && streaming.receiverVolume != null) {
                    item {
                        SettingsCard {
                            VolumeControl("全部音箱音量", streaming.receiverVolume) { onSetVolume(it, null) }
                        }
                    }
                }
                item {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(top = 4.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(
                                "音箱",
                                style = MaterialTheme.typography.titleLarge,
                                modifier = Modifier.semantics { heading() },
                            )
                            if (targets.isNotEmpty()) {
                                Text(
                                    if (split) "已选 $selectedCount / 2 台" else "发现 ${targets.size} 个投送目标",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                        TextButton(onClick = onRefresh, enabled = !scanning) {
                            if (scanning) {
                                CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                            } else {
                                Icon(Icons.Default.Refresh, contentDescription = null, modifier = Modifier.size(18.dp))
                            }
                            Text(if (scanning) "搜索中" else "重新搜索", Modifier.padding(start = 8.dp))
                        }
                    }
                    if (split && targets.isNotEmpty()) {
                        Text(
                            "按顺序选择两台独立音箱，第一台为左声道，第二台为右声道。",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(top = 4.dp),
                        )
                    }
                }
                if (!scanning && discoveryError != null) {
                    item {
                        Notice(discoveryError, isError = true)
                    }
                }
                if (targets.isEmpty()) {
                    item {
                        Card(
                            modifier = Modifier.fillMaxWidth(),
                            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
                        ) {
                            Column(
                                modifier = Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 24.dp),
                                horizontalAlignment = Alignment.CenterHorizontally,
                                verticalArrangement = Arrangement.spacedBy(8.dp),
                            ) {
                                CastGlyph()
                                Text(
                                    if (scanning) "正在查找同网音箱" else "暂未发现音箱",
                                    style = MaterialTheme.typography.titleMedium,
                                )
                                Text(
                                    "请将手机和音箱连接到同一个 Wi-Fi。",
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }
                }
                items(targets, key = SpeakerTarget::id) { target ->
                    val selectedIndex = selectedTargetIds.indexOf(target.id)
                    val selected = selectedIndex >= 0
                    val controls = streaming?.receiverControls.orEmpty()
                    val controlIndices = target.renderers.mapNotNull { renderer ->
                        controls.indexOfFirst { control ->
                            control.rendererId == renderer.id ||
                                control.rendererHost.isNotBlank() && control.rendererHost == renderer.host
                        }.takeIf { it >= 0 }
                    }.distinct()
                    val controlled = controlIndices.map { controls[it] }
                    val volume = controlled.mapNotNull { it.volume }.minOrNull()
                    val details = if (target.isStereoPair) stereoPairDetails(target) else null
                    val stateText = when {
                        !target.isAvailable -> "等待另一台音箱上线"
                        target.isStereoPair && controlled.size == 1 -> "一台音箱已连接"
                        split && selected -> if (selectedIndex == 0) "左声道" else "右声道"
                        controlled.isNotEmpty() -> if (controlled.all { it.paused }) "已暂停" else "播放中"
                        selected -> "已选中"
                        else -> null
                    }
                    ReceiverCard(
                        name = target.name,
                        selected = selected,
                        enabled = !preparing && (target.isAvailable || selected) &&
                            (streaming == null || protocol == CastProtocol.MIPLAY && !split),
                        multiple = protocol == CastProtocol.MIPLAY,
                        supportingText = listOfNotNull(details, stateText).joinToString("\n").ifBlank { null },
                        volume = volume,
                        volumeLabel = if (target.isStereoPair) "组合音量" else "${target.name}音量",
                        onToggle = { onToggleTarget(target) },
                        onVolume = { value ->
                            if (target.isStereoPair) {
                                val receiverIds = controlled.map { it.rendererId }.filter { it.isNotBlank() }
                                if (receiverIds.isNotEmpty()) onSetGroupVolume(value, receiverIds)
                            } else {
                                controlIndices.firstOrNull()?.let { onSetVolume(value, it) }
                            }
                        },
                    )
                }
            }
        }
    }
}

private fun stereoPairDetails(target: SpeakerTarget): String = buildString {
    append("立体声组合")
    val left = target.renderers.firstOrNull { it.miPlayIdentity?.channel == "left" }
    val right = target.renderers.firstOrNull { it.miPlayIdentity?.channel == "right" }
    if (left != null && left.name != target.name) append("\n左声道 · ${left.name}")
    if (right != null && right.name != target.name) append("\n右声道 · ${right.name}")
}

@Composable
private fun SessionCard(status: CastStatus, protocol: CastProtocol) {
    val title = when (status) {
        CastStatus.Idle -> "投送手机声音"
        is CastStatus.Preparing -> "正在连接"
        is CastStatus.Streaming -> if (status.receiverPaused) "投送已暂停" else "正在投送"
        is CastStatus.Error -> "投送遇到问题"
    }
    val detail = when (status) {
        CastStatus.Idle -> "选择音箱后，点按开始投送。"
        is CastStatus.Preparing -> status.message
        is CastStatus.Streaming -> status.rendererName
        is CastStatus.Error -> status.message
    }
    val scheme = MaterialTheme.colorScheme
    val error = status is CastStatus.Error
    val active = status is CastStatus.Streaming
    val container = when {
        error -> scheme.errorContainer
        active -> scheme.primaryContainer
        else -> scheme.secondaryContainer
    }
    val content = when {
        error -> scheme.onErrorContainer
        active -> scheme.onPrimaryContainer
        else -> scheme.onSecondaryContainer
    }
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = MaterialTheme.shapes.extraLarge,
        colors = CardDefaults.cardColors(containerColor = container, contentColor = content),
    ) {
        Column(
            modifier = Modifier.fillMaxWidth().padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                CastGlyph(
                    container = if (error) scheme.error else scheme.primary,
                    tint = if (error) scheme.onError else scheme.onPrimary,
                )
                Text(
                    protocol.label,
                    modifier = Modifier.padding(start = 12.dp),
                    style = MaterialTheme.typography.labelLarge,
                )
            }
            Text(title, style = MaterialTheme.typography.headlineSmall)
            Text(detail, style = MaterialTheme.typography.bodyMedium)
        }
    }
}

@Composable
private fun CastGlyph(
    container: androidx.compose.ui.graphics.Color = MaterialTheme.colorScheme.primaryContainer,
    tint: androidx.compose.ui.graphics.Color = MaterialTheme.colorScheme.onPrimaryContainer,
) {
    Box(
        modifier = Modifier.size(44.dp).background(container, MaterialTheme.shapes.medium),
        contentAlignment = Alignment.Center,
    ) {
        Icon(painterResource(R.drawable.ic_cast), contentDescription = null, tint = tint)
    }
}

@Composable
private fun CastActionBar(
    status: CastStatus,
    canStart: Boolean,
    onStart: () -> Unit,
    onStop: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit,
) {
    Surface(tonalElevation = 2.dp) {
        Box(
            Modifier.fillMaxWidth().navigationBarsPadding().padding(horizontal = 20.dp, vertical = 12.dp),
            contentAlignment = Alignment.Center,
        ) {
            Row(
                modifier = Modifier.widthIn(max = 600.dp).fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                when (status) {
                    is CastStatus.Streaming -> {
                        if (status.supportsPlaybackControl) {
                            FilledTonalButton(
                                onClick = if (status.receiverPaused) onResume else onPause,
                                modifier = Modifier.weight(1f).heightIn(min = 48.dp),
                            ) { Text(if (status.receiverPaused) "继续" else "暂停") }
                        }
                        OutlinedButton(
                            onClick = onStop,
                            modifier = Modifier.weight(1f).heightIn(min = 48.dp),
                            colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error),
                        ) { Text("停止投送") }
                    }
                    is CastStatus.Preparing -> {
                        OutlinedButton(onClick = onStop, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                            Text("取消连接")
                        }
                    }
                    else -> {
                        Button(
                            onClick = onStart,
                            enabled = canStart,
                            modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
                        ) { Text("开始投送") }
                    }
                }
            }
        }
    }
}

@Composable
private fun ReceiverCard(
    name: String,
    selected: Boolean,
    enabled: Boolean,
    multiple: Boolean,
    supportingText: String?,
    volume: Int?,
    volumeLabel: String,
    onToggle: () -> Unit,
    onVolume: (Int) -> Unit,
) {
    val scheme = MaterialTheme.colorScheme
    val select = if (multiple) {
        Modifier.toggleable(selected, enabled, Role.Checkbox) { onToggle() }
    } else {
        Modifier.selectable(selected, enabled, Role.RadioButton, onToggle)
    }
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = MaterialTheme.shapes.large,
        colors = CardDefaults.cardColors(
            containerColor = if (selected) scheme.primaryContainer else scheme.surfaceContainerLow,
            contentColor = if (selected) scheme.onPrimaryContainer else scheme.onSurface,
        ),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().then(select).padding(horizontal = 16.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            CastGlyph(
                container = if (selected) scheme.primary else scheme.secondaryContainer,
                tint = if (selected) scheme.onPrimary else scheme.onSecondaryContainer,
            )
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(
                    name,
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                if (supportingText != null) {
                    Text(supportingText, style = MaterialTheme.typography.bodySmall)
                }
            }
            if (multiple) Checkbox(checked = selected, onCheckedChange = null, enabled = enabled)
            else RadioButton(selected = selected, onClick = null, enabled = enabled)
        }
        if (volume != null) {
            HorizontalDivider(color = scheme.outlineVariant, modifier = Modifier.padding(horizontal = 16.dp))
            Column(Modifier.padding(start = 16.dp, end = 16.dp, top = 12.dp)) {
                VolumeControl(volumeLabel, volume, onVolume)
            }
        }
    }
}

@Composable
private fun VolumeControl(label: String, volume: Int, onVolume: (Int) -> Unit) {
    var target by remember(volume) { mutableFloatStateOf(volume.coerceIn(0, 100).toFloat()) }
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
        Text(label, modifier = Modifier.weight(1f), style = MaterialTheme.typography.labelLarge)
        Text("${target.roundToInt()}%", style = MaterialTheme.typography.labelLarge)
    }
    Slider(
        value = target,
        onValueChange = { target = it },
        onValueChangeFinished = { onVolume(target.roundToInt()) },
        valueRange = 0f..100f,
        modifier = Modifier.fillMaxWidth().semantics { contentDescription = label },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CastSettings(
    protocol: CastProtocol,
    captureSources: List<CaptureSource>,
    selectedCaptureSource: CaptureSource?,
    shizukuCaptureAvailable: Boolean,
    useShizukuCapture: Boolean,
    muteLocalSpeaker: Boolean,
    localMuteControlEnabled: Boolean,
    localSpeakerMuteError: String?,
    channelRoute: AudioChannelRoute,
    stereoSplit: Boolean,
    playbackDelayMilliseconds: Float,
    casting: Boolean,
    onBack: () -> Unit,
    onProtocol: (CastProtocol) -> Unit,
    onCaptureSource: (CaptureSource) -> Unit,
    onShizukuCapture: (Boolean) -> Unit,
    onLocalSpeakerMute: (Boolean) -> Unit,
    onChannelRoute: (AudioChannelRoute) -> Unit,
    onStereoSplit: (Boolean) -> Unit,
    onPlaybackDelayMilliseconds: (Float) -> Unit,
) {
    var pickingSource by rememberSaveable { mutableStateOf(false) }
    BackHandler(onBack = onBack)
    Scaffold(
        contentWindowInsets = WindowInsets.safeDrawing,
        topBar = {
            TopAppBar(
                title = { Text("设置") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "返回")
                    }
                },
            )
        },
    ) { padding ->
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
            LazyColumn(
                modifier = Modifier
                    .widthIn(max = 640.dp)
                    .fillMaxSize()
                    .padding(padding)
                    .consumeWindowInsets(padding),
                contentPadding = PaddingValues(start = 20.dp, top = 12.dp, end = 20.dp, bottom = 24.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                if (casting) item { Notice("停止投送后可更改传输协议和音频来源。") }
                item {
                    SettingsHeading("传输协议")
                    SettingsCard {
                        Column(Modifier.selectableGroup()) {
                            SettingChoice(
                                "MiPlay",
                                "用于支持小米妙播的音箱。",
                                protocol == CastProtocol.MIPLAY,
                                !casting,
                            ) { onProtocol(CastProtocol.MIPLAY) }
                            SettingChoice(
                                "DLNA",
                                "用于兼容 UPnP / DLNA 的设备。",
                                protocol == CastProtocol.DLNA,
                                !casting,
                            ) { onProtocol(CastProtocol.DLNA) }
                        }
                    }
                }
                item {
                    SettingsHeading("音频来源")
                    SettingsCard {
                        Text(
                            "选择要投送声音的应用",
                            style = MaterialTheme.typography.titleSmall,
                        )
                        OutlinedButton(
                            onClick = { pickingSource = true },
                            modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
                            enabled = !casting,
                        ) {
                            Text(selectedCaptureSource?.label ?: "请重新选择音频来源")
                        }
                        SettingsExplanation("通话、受保护内容和禁止捕获的应用无法投送。")
                        if (shizukuCaptureAvailable) {
                            HorizontalDivider()
                            Column(Modifier.selectableGroup()) {
                                SettingChoice(
                                    "Android 系统授权", "投送前弹出系统音频捕获确认。", !useShizukuCapture, !casting,
                                ) { onShizukuCapture(false) }
                                SettingChoice(
                                    "Shizuku 音频捕获", "需要启动并授权 Shizuku。",
                                    useShizukuCapture, !casting,
                                ) { onShizukuCapture(true) }
                            }
                            if (useShizukuCapture) {
                                HorizontalDivider()
                                Row(
                                    modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)
                                        .toggleable(muteLocalSpeaker, localMuteControlEnabled, Role.Switch, onLocalSpeakerMute),
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                                ) {
                                    Column(Modifier.weight(1f)) {
                                        Text("仅音箱播放", style = MaterialTheme.typography.titleSmall)
                                        SettingsExplanation("静音手机扬声器。停止投送后恢复原音量。")
                                    }
                                    Switch(checked = muteLocalSpeaker, onCheckedChange = null, enabled = localMuteControlEnabled)
                                }
                                localSpeakerMuteError?.let { SettingsExplanation(it) }
                            }
                        } else {
                            SettingsExplanation("投送前需确认 Android 的音频捕获授权。")
                        }
                    }
                }
                if (protocol == CastProtocol.MIPLAY) {
                    item {
                        SettingsHeading("声道与延迟")
                        SettingsCard {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .heightIn(min = 48.dp)
                                    .toggleable(stereoSplit, !casting, Role.Switch, onStereoSplit),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(12.dp),
                            ) {
                                Column(Modifier.weight(1f)) {
                                    Text("手动立体声拆分", style = MaterialTheme.typography.titleSmall)
                                    SettingsExplanation("按选择顺序分配两台音箱的左右声道。关闭后自动显示已有的立体声组合。")
                                }
                                Switch(checked = stereoSplit, onCheckedChange = null, enabled = !casting)
                            }
                            if (!stereoSplit) {
                                HorizontalDivider()
                                Column(Modifier.selectableGroup()) {
                                    SettingChoice("原始立体声", null, channelRoute == AudioChannelRoute.Stereo, !casting) {
                                        onChannelRoute(AudioChannelRoute.Stereo)
                                    }
                                    SettingChoice("左声道", null, channelRoute == AudioChannelRoute.LeftAsMono, !casting) {
                                        onChannelRoute(AudioChannelRoute.LeftAsMono)
                                    }
                                    SettingChoice("右声道", null, channelRoute == AudioChannelRoute.RightAsMono, !casting) {
                                        onChannelRoute(AudioChannelRoute.RightAsMono)
                                    }
                                }
                            }
                            HorizontalDivider()
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                            ) {
                                Text("播放预留时间", style = MaterialTheme.typography.titleSmall)
                                Text("${playbackDelayMilliseconds.roundToInt()} ms", style = MaterialTheme.typography.labelLarge)
                            }
                            Slider(
                                value = playbackDelayMilliseconds.coerceIn(0f, 2000f),
                                onValueChange = onPlaybackDelayMilliseconds,
                                valueRange = 0f..2000f,
                                steps = 39,
                                enabled = !casting,
                                modifier = Modifier.fillMaxWidth().semantics { contentDescription = "播放预留时间" },
                            )
                            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                listOf(500f, 1000f, 1500f).forEach { preset ->
                                    FilterChip(
                                        selected = playbackDelayMilliseconds == preset,
                                        onClick = { onPlaybackDelayMilliseconds(preset) },
                                        enabled = !casting,
                                        label = { Text("${preset.toInt()} ms") },
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    if (pickingSource && !casting) {
        CaptureSourceDialog(
            sources = captureSources,
            selectedSource = selectedCaptureSource,
            onDismiss = { pickingSource = false },
            onSelect = {
                onCaptureSource(it)
                pickingSource = false
            },
        )
    }
}

@Composable
private fun CaptureSourceDialog(
    sources: List<CaptureSource>,
    selectedSource: CaptureSource?,
    onDismiss: () -> Unit,
    onSelect: (CaptureSource) -> Unit,
) {
    var searching by rememberSaveable { mutableStateOf(false) }
    var search by rememberSaveable { mutableStateOf("") }
    val query = search.trim()
    val systemSource = sources.firstOrNull { it.isSystemWide }
    val apps = sources.filterNot { it.isSystemWide }
    val visibleApps = when {
        !searching -> apps.filter { it.isSuggestedMedia || it == selectedSource }
        query.isBlank() -> emptyList()
        else -> apps.filter { it.label.contains(query, ignoreCase = true) }
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("选择音频来源") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (systemSource != null) {
                    CaptureSourceRow(systemSource, selectedSource == systemSource, onSelect)
                }
                if (searching) {
                    OutlinedTextField(
                        value = search,
                        onValueChange = { search = it },
                        label = { Text("应用名称") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                } else {
                    HorizontalDivider()
                    Text("建议的媒体应用", style = MaterialTheme.typography.labelLarge)
                    SettingsExplanation("根据应用声明的音乐、视频分类和常见播放器筛选。是否能捕获取决于应用和正在播放的内容。")
                }
                if (visibleApps.isEmpty()) {
                    SettingsExplanation(when {
                        !searching -> "没有找到建议的媒体应用。"
                        query.isBlank() -> "输入应用名称后显示结果。"
                        else -> "没有找到匹配的应用。"
                    })
                } else {
                    LazyColumn(Modifier.heightIn(max = 260.dp).selectableGroup()) {
                        items(visibleApps, key = { it.packageName ?: it.label }) { source ->
                            CaptureSourceRow(source, source == selectedSource, onSelect)
                        }
                    }
                }
                if (!searching) {
                    TextButton(onClick = { searching = true }) {
                        Icon(Icons.Default.Search, contentDescription = null, modifier = Modifier.size(18.dp))
                        Text("按名称搜索其他应用", Modifier.padding(start = 8.dp))
                    }
                }
            }
        },
        dismissButton = if (searching) {
            {
                TextButton(onClick = {
                    searching = false
                    search = ""
                }) { Text("返回建议") }
            }
        } else null,
        confirmButton = { TextButton(onClick = onDismiss) { Text("取消") } },
    )
}

@Composable
private fun CaptureSourceRow(source: CaptureSource, selected: Boolean, onSelect: (CaptureSource) -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp)
            .selectable(selected, role = Role.RadioButton, onClick = { onSelect(source) }),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(source.label, modifier = Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
        if (selected) {
            Icon(Icons.Default.Check, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
        }
    }
}

@Composable
private fun SettingChoice(
    title: String,
    summary: String?,
    selected: Boolean,
    enabled: Boolean,
    onClick: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp)
            .selectable(selected, enabled, Role.RadioButton, onClick)
            .padding(vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        RadioButton(selected = selected, onClick = null, enabled = enabled)
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.titleSmall)
            if (summary != null) SettingsExplanation(summary)
        }
    }
}

@Composable
private fun SettingsHeading(title: String) {
    Text(
        title,
        modifier = Modifier.padding(start = 4.dp, top = 4.dp, bottom = 8.dp).semantics { heading() },
        style = MaterialTheme.typography.titleMedium,
    )
}

@Composable
private fun SettingsCard(content: @Composable () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = MaterialTheme.shapes.large,
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
    ) {
        Column(
            modifier = Modifier.fillMaxWidth().padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) { content() }
    }
}

@Composable
private fun SettingsExplanation(text: String) {
    Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable
private fun Notice(message: String, isError: Boolean = false) {
    val scheme = MaterialTheme.colorScheme
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = MaterialTheme.shapes.large,
        color = if (isError) scheme.errorContainer else scheme.surfaceContainer,
        contentColor = if (isError) scheme.onErrorContainer else scheme.onSurface,
    ) {
        Text(message, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(16.dp))
    }
}

private val CastProtocol.label: String
    get() = if (this == CastProtocol.MIPLAY) "MiPlay" else "DLNA"
