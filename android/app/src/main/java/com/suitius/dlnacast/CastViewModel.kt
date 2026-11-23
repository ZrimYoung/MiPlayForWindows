package com.suitius.dlnacast

import android.app.Application
import android.content.Intent
import android.content.pm.PackageManager.NameNotFoundException
import android.os.Build
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.suitius.dlnacast.audio.AudioChannelRoute
import com.suitius.dlnacast.audio.CaptureSource
import com.suitius.dlnacast.audio.CaptureSourceCatalog
import com.suitius.dlnacast.audio.ShizukuPlaybackAudioCapture
import com.suitius.dlnacast.model.DlnaRenderer
import com.suitius.dlnacast.model.SpeakerTarget
import com.suitius.dlnacast.model.speakerTargets
import com.suitius.dlnacast.network.SsdpDiscovery
import com.suitius.dlnacast.service.CastPreferences
import com.suitius.dlnacast.service.CastProtocol
import com.suitius.dlnacast.service.CastService
import com.suitius.dlnacast.service.CastStateStore
import com.suitius.dlnacast.service.CastStatus
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

internal data class CastRequest(
    val renderers: List<DlnaRenderer>,
    val protocol: CastProtocol,
    val captureUid: Int?,
    val routes: List<AudioChannelRoute>,
    val delayMicroseconds: Int,
    val muteLocalSpeaker: Boolean,
)

internal class CastViewModel(private val app: Application) : AndroidViewModel(app) {
    private val discovery = SsdpDiscovery(app)
    private val preferences = CastPreferences(app)
    val captureSources = CaptureSourceCatalog.load(app)

    var settingsOpen by mutableStateOf(false)
    var protocol by mutableStateOf(preferences.protocol)
        private set
    private var captureUid by mutableStateOf(preferences.captureUid)
    private var capturePackageName by mutableStateOf(preferences.capturePackageName)
    var useShizukuCapture by mutableStateOf(
        Build.VERSION.SDK_INT >= ShizukuPlaybackAudioCapture.MINIMUM_SDK && preferences.useShizukuCapture,
    )
        private set
    var muteLocalSpeaker by mutableStateOf(preferences.muteLocalSpeaker)
        private set
    var channelRoute by mutableStateOf(preferences.channelRoute)
        private set
    var stereoSplit by mutableStateOf(preferences.stereoSplit)
        private set
    var delayMilliseconds by mutableStateOf(preferences.playbackDelayMilliseconds)
        private set
    var scanning by mutableStateOf(false)
        private set
    var discoveryError by mutableStateOf<String?>(null)
        private set
    var selectedTargetIds by mutableStateOf<List<String>>(emptyList())
        private set

    private var renderers by mutableStateOf<List<DlnaRenderer>>(emptyList())
    private var hasScanned = false
    private var pendingRequest by mutableStateOf<CastRequest?>(null)
    private var lastActive = false
    val awaitingAuthorization get() = pendingRequest != null
    val selectedCaptureSource: CaptureSource?
        get() = captureSources.firstOrNull { source ->
            source.uid == captureUid && (source.packageName == capturePackageName ||
                capturePackageName == null && !useShizukuCapture)
        }

    fun refreshIfNeeded() {
        if (!hasScanned) refresh()
    }

    fun refresh() {
        if (scanning) return
        hasScanned = true
        scanning = true
        discoveryError = null
        viewModelScope.launch {
            try {
                val status = CastStateStore.status.value
                val activeHosts = (status as? CastStatus.Streaming)?.renderers.orEmpty().mapTo(HashSet(), DlnaRenderer::host)
                if (status is CastStatus.Preparing) return@launch
                renderers = discovery.discover(activeHosts = activeHosts) { host ->
                    when (val current = CastStateStore.status.value) {
                        is CastStatus.Preparing -> false
                        is CastStatus.Streaming -> current.renderers.none { it.host == host }
                        else -> true
                    }
                }
                val current = CastStateStore.status.value
                if (current is CastStatus.Streaming) updateSelection(current)
                else if (!castInProgress()) {
                    val availableIds = visibleTargets(current).filter(SpeakerTarget::isAvailable).mapTo(HashSet(), SpeakerTarget::id)
                    selectedTargetIds = selectedTargetIds.filter { it in availableIds }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (exception: Exception) {
                discoveryError = "搜索音箱失败，请重试。"
                android.util.Log.w("DLNACast.Discovery", "Speaker discovery failed", exception)
            } finally {
                scanning = false
            }
        }
    }

    fun visibleTargets(status: CastStatus): List<SpeakerTarget> {
        val active = status as? CastStatus.Streaming
        val selectedProtocol = active?.protocol ?: protocol
        val compatible = renderers.filter { renderer ->
            when (selectedProtocol) {
                CastProtocol.MIPLAY -> renderer.supportsMiPlay
                CastProtocol.DLNA -> renderer.avTransportControlUrl.isNotBlank()
            }
        }
        val activeRenderers = active?.renderers.orEmpty().map { renderer ->
            val known = compatible.firstOrNull { it.host == renderer.host }
            if (renderer.miPlayIdentity != null || known == null) renderer
            else renderer.copy(name = known.name, miPlayIdentity = known.miPlayIdentity)
        }
        val manualSplit = active?.stereoSplit ?: stereoSplit
        return speakerTargets(
            (activeRenderers + compatible).distinctBy(DlnaRenderer::host),
            useNativeGroups = selectedProtocol == CastProtocol.MIPLAY && !manualSplit,
        )
    }

    fun updateSelection(status: CastStatus) {
        when (status) {
            is CastStatus.Streaming -> {
                lastActive = true
                val activeIds = status.renderers.mapTo(HashSet(), DlnaRenderer::id)
                selectedTargetIds = visibleTargets(status).filter { target -> target.renderers.any { it.id in activeIds } }
                    .map(SpeakerTarget::id)
                muteLocalSpeaker = preferences.muteLocalSpeaker
            }
            is CastStatus.Idle -> {
                if (lastActive) selectedTargetIds = emptyList()
                lastActive = false
            }
            is CastStatus.Error -> lastActive = false
            is CastStatus.Preparing -> Unit
        }
    }

    fun toggleTarget(target: SpeakerTarget, send: (Intent) -> Unit) {
        val status = CastStateStore.status.value
        if (awaitingAuthorization || status is CastStatus.Preparing) return
        val selected = target.id in selectedTargetIds
        if (status is CastStatus.Streaming) {
            if (status.protocol != CastProtocol.MIPLAY || status.stereoSplit) return
            val activeIds = status.renderers.mapTo(HashSet(), DlnaRenderer::id)
            val targetIds = target.renderers.mapTo(HashSet(), DlnaRenderer::id)
            when {
                selected && activeIds.all { it in targetIds } -> send(CastService.stopIntent(app))
                selected -> send(CastService.removeRenderersIntent(app, targetIds.toList()))
                target.isAvailable && activeIds.size + targetIds.size <= MAX_RECEIVERS ->
                    send(CastService.addRenderersIntent(app, target.renderers))
            }
            return
        }
        val selectedReceivers = visibleTargets(status).filter { it.id in selectedTargetIds }.sumOf { it.renderers.size }
        selectedTargetIds = when {
            selected -> selectedTargetIds - target.id
            !target.isAvailable -> selectedTargetIds
            protocol == CastProtocol.DLNA -> listOf(target.id)
            selectedReceivers + target.renderers.size <= (if (stereoSplit) 2 else MAX_RECEIVERS) -> selectedTargetIds + target.id
            else -> selectedTargetIds
        }
    }

    fun prepareRequest(): Boolean {
        if (castInProgress()) return false
        val available = visibleTargets(CastStateStore.status.value).filter(SpeakerTarget::isAvailable).associateBy(SpeakerTarget::id)
        val selection = selectedTargetIds.mapNotNull(available::get).flatMap(SpeakerTarget::renderers)
        val split = protocol == CastProtocol.MIPLAY && stereoSplit
        if (selection.isEmpty() || (split && selection.size != 2)) return false
        val audioSource = selectedCaptureSource
        if (audioSource == null || !sourceStillInstalled(audioSource)) {
            rejectAuthorization("此前选择的应用已不可用，请在设置中重新选择音频来源。")
            return false
        }
        pendingRequest = CastRequest(
            selection,
            protocol,
            audioSource.uid,
            if (split) listOf(AudioChannelRoute.LeftAsMono, AudioChannelRoute.RightAsMono)
            else List(selection.size) { channelRoute },
            delayMilliseconds.toInt() * 1_000,
            useShizukuCapture && muteLocalSpeaker,
        )
        return true
    }

    fun takePendingRequest(): CastRequest? {
        val request = pendingRequest ?: return null
        pendingRequest = null
        if (selectedCaptureSource?.let(::sourceStillInstalled) != true) {
            rejectAuthorization("此前选择的应用已不可用，请在设置中重新选择音频来源。")
            return null
        }
        return request
    }

    fun cancelAuthorization() {
        pendingRequest = null
    }

    fun rejectAuthorization(message: String) {
        pendingRequest = null
        CastStateStore.update(CastStatus.Error(message))
    }

    fun changeProtocol(value: CastProtocol) = changeOptions {
        if (protocol != value) selectedTargetIds = emptyList()
        protocol = value
    }

    fun changeCaptureSource(value: CaptureSource) = changeOptions {
        captureUid = value.uid
        capturePackageName = value.packageName
    }
    fun changeShizukuCapture(value: Boolean) = changeOptions { useShizukuCapture = value }
    fun changeLocalSpeakerMute(muted: Boolean, send: (Intent) -> Unit) {
        if (!useShizukuCapture || awaitingAuthorization) return
        val status = CastStateStore.status.value
        if (status is CastStatus.Streaming && status.localSpeakerMuted != null) {
            send(CastService.setLocalSpeakerMuteIntent(app, muted))
        } else if (status !is CastStatus.Preparing && status !is CastStatus.Streaming) {
            muteLocalSpeaker = muted
            preferences.saveLocalSpeakerMute(muted)
        }
    }
    fun changeChannelRoute(value: AudioChannelRoute) = changeOptions { channelRoute = value }
    fun changeStereoSplit(value: Boolean) = changeOptions {
        if (stereoSplit != value) selectedTargetIds = emptyList()
        stereoSplit = value
    }
    fun changeDelay(value: Float) = changeOptions { delayMilliseconds = value.coerceIn(0f, 2_000f) }

    private inline fun changeOptions(change: () -> Unit) {
        if (castInProgress()) return
        change()
        preferences.saveOptions(
            protocol, captureUid, capturePackageName, channelRoute, delayMilliseconds.toInt(), stereoSplit, useShizukuCapture,
        )
    }

    private fun sourceStillInstalled(source: CaptureSource): Boolean {
        val packageName = source.packageName ?: return source.isSystemWide
        return try {
            app.packageManager.getPackageUid(packageName, 0) == source.uid
        } catch (_: NameNotFoundException) {
            false
        }
    }

    private fun castInProgress(): Boolean {
        val status = CastStateStore.status.value
        return awaitingAuthorization || status is CastStatus.Preparing || status is CastStatus.Streaming
    }

    private companion object {
        const val MAX_RECEIVERS = 63
    }
}
