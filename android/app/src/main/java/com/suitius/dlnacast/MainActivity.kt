package com.suitius.dlnacast

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.suitius.dlnacast.audio.ShizukuPlaybackAudioCapture
import com.suitius.dlnacast.service.CastService
import com.suitius.dlnacast.service.CastStateStore
import com.suitius.dlnacast.service.CastStatus
import com.suitius.dlnacast.systemui.SystemUiBridgeProvider
import com.suitius.dlnacast.systemui.systemUiStartIntent
import com.suitius.dlnacast.ui.CastHome
import com.suitius.dlnacast.ui.CastSettings
import com.suitius.dlnacast.ui.theme.DLNACastTheme
import rikka.shizuku.Shizuku

class MainActivity : ComponentActivity() {
    private val model: CastViewModel by viewModels()
    private val shizukuPermissionListener = Shizuku.OnRequestPermissionResultListener { requestCode, grantResult ->
        if (requestCode == SHIZUKU_PERMISSION_REQUEST && model.awaitingAuthorization && model.useShizukuCapture) {
            if (grantResult == PackageManager.PERMISSION_GRANTED) startShizukuCast()
            else model.rejectAuthorization("未获 Shizuku 授权。请在 Shizuku 的应用管理中允许 MiPlay Cast。")
        }
    }
    private val shizukuDeathListener = Shizuku.OnBinderDeadListener {
        if (model.awaitingAuthorization && model.useShizukuCapture) {
            model.rejectAuthorization("Shizuku 已停止。请启动服务后重试投送。")
        }
    }

    private val requestRecordPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) requestProjection()
        else model.rejectAuthorization("请允许录音权限，Android 才能捕获应用播放的声音。")
    }

    private val requestScreenCapture = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val request = model.takePendingRequest() ?: return@registerForActivityResult
        val authorization = result.data
        if (result.resultCode != RESULT_OK || authorization == null) {
            model.rejectAuthorization("已取消系统音频授权。可以再次点选「开始投送」。")
            return@registerForActivityResult
        }
        startCastService(CastService.startIntent(
            this, request.renderers, request.protocol, result.resultCode, authorization,
            request.captureUid, request.routes, request.delayMicroseconds,
        ))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Shizuku.addRequestPermissionResultListener(shizukuPermissionListener)
        Shizuku.addBinderDeadListener(shizukuDeathListener)
        enableEdgeToEdge()
        setContent {
            DLNACastTheme {
                val serviceStatus by CastStateStore.status.collectAsStateWithLifecycle()
                val status = if (model.awaitingAuthorization) {
                    CastStatus.Preparing(if (model.useShizukuCapture) "请确认 Shizuku 授权" else "请确认 Android 的音频捕获授权")
                } else serviceStatus
                LaunchedEffect(Unit) { model.refreshIfNeeded() }
                LaunchedEffect(serviceStatus) { model.updateSelection(serviceStatus) }
                if (model.settingsOpen) {
                    val active = status as? CastStatus.Streaming
                    CastSettings(
                        protocol = model.protocol, captureSources = model.captureSources,
                        selectedCaptureSource = model.selectedCaptureSource, channelRoute = model.channelRoute,
                        shizukuCaptureAvailable = Build.VERSION.SDK_INT >= ShizukuPlaybackAudioCapture.MINIMUM_SDK,
                        useShizukuCapture = model.useShizukuCapture,
                        muteLocalSpeaker = active?.localSpeakerMuted ?: model.muteLocalSpeaker,
                        localMuteControlEnabled = status !is CastStatus.Preparing &&
                            (active == null || active.localSpeakerMuted != null),
                        localSpeakerMuteError = active?.localSpeakerMuteError,
                        stereoSplit = model.stereoSplit, playbackDelayMilliseconds = model.delayMilliseconds,
                        casting = status is CastStatus.Preparing || status is CastStatus.Streaming,
                        onBack = { model.settingsOpen = false },
                        onProtocol = model::changeProtocol,
                        onCaptureSource = model::changeCaptureSource,
                        onShizukuCapture = model::changeShizukuCapture,
                        onLocalSpeakerMute = { muted -> model.changeLocalSpeakerMute(muted) { startService(it) } },
                        onChannelRoute = model::changeChannelRoute,
                        onStereoSplit = model::changeStereoSplit,
                        onPlaybackDelayMilliseconds = model::changeDelay,
                    )
                } else {
                    val active = serviceStatus as? CastStatus.Streaming
                    CastHome(
                        status = status,
                        protocol = active?.protocol ?: model.protocol,
                        targets = model.visibleTargets(serviceStatus),
                        selectedTargetIds = model.selectedTargetIds,
                        scanning = model.scanning, discoveryError = model.discoveryError,
                        stereoSplit = active?.stereoSplit ?: model.stereoSplit,
                        onOpenSettings = { model.settingsOpen = true },
                        onRefresh = model::refresh,
                        onToggleTarget = { target -> model.toggleTarget(target) { startService(it) } },
                        onStart = ::authorizeCast,
                        onStop = {
                            if (model.awaitingAuthorization) model.cancelAuthorization()
                            else startService(CastService.stopIntent(this))
                        },
                        onPause = { startService(CastService.pauseIntent(this)) },
                        onResume = { startService(CastService.resumeIntent(this)) },
                        onSetVolume = { volume, index -> startService(CastService.setVolumeIntent(this, volume, index)) },
                        onSetGroupVolume = { volume, ids -> startService(CastService.setGroupVolumeIntent(this, volume, ids)) },
                    )
                }
            }
        }
        handleSystemUiSelection(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleSystemUiSelection(intent)
    }

    override fun onDestroy() {
        Shizuku.removeRequestPermissionResultListener(shizukuPermissionListener)
        Shizuku.removeBinderDeadListener(shizukuDeathListener)
        super.onDestroy()
    }

    private fun authorizeCast() {
        if (!model.prepareRequest()) return
        if (model.useShizukuCapture) {
            authorizeShizukuCast()
            return
        }
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            requestProjection()
        } else {
            requestRecordPermission.launch(Manifest.permission.RECORD_AUDIO)
        }
    }

    private fun authorizeShizukuCast() {
        try {
            when {
                !Shizuku.pingBinder() -> model.rejectAuthorization("请安装 Shizuku 并通过调试模式启动服务，再重试投送。")
                Shizuku.getUid() != 2_000 -> model.rejectAuthorization("请通过调试模式启动 Shizuku，再重试投送。")
                Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED -> startShizukuCast()
                Shizuku.shouldShowRequestPermissionRationale() ->
                    model.rejectAuthorization("请在 Shizuku 的应用管理中允许 MiPlay Cast，再重试投送。")
                else -> Shizuku.requestPermission(SHIZUKU_PERMISSION_REQUEST)
            }
        } catch (error: Exception) {
            model.rejectAuthorization(error.message ?: "无法连接 Shizuku。")
        }
    }

    private fun startShizukuCast() {
        val request = model.takePendingRequest() ?: return
        startCastService(CastService.shizukuStartIntent(
            this, request.renderers, request.protocol, request.captureUid, request.routes, request.delayMicroseconds,
            request.muteLocalSpeaker,
        ))
    }

    private fun startCastService(intent: Intent) {
        CastStateStore.update(CastStatus.Preparing("正在启动音频捕获…"))
        try {
            ContextCompat.startForegroundService(this, intent)
        } catch (exception: Exception) {
            CastStateStore.update(CastStatus.Error(exception.message ?: "无法启动投送服务。"))
        }
    }

    private fun requestProjection() {
        if (!model.awaitingAuthorization) return
        try {
            val manager = getSystemService(MediaProjectionManager::class.java)
            // Playback capture uses the system mix. The app never creates a virtual display.
            val request = if (Build.VERSION.SDK_INT >= 34) {
                manager.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
            } else {
                manager.createScreenCaptureIntent()
            }
            requestScreenCapture.launch(request)
        } catch (exception: Exception) {
            model.rejectAuthorization(exception.message ?: "无法打开 Android 音频捕获授权。")
        }
    }

    private fun handleSystemUiSelection(intent: Intent?) {
        if (intent?.action != SystemUiBridgeProvider.ACTION_SYSTEM_UI_SELECTION) return
        val nonce = intent.getStringExtra(SystemUiBridgeProvider.EXTRA_NONCE)
        val renderer = SystemUiBridgeProvider.consumePendingSelection(nonce) ?: return
        val request = systemUiStartIntent(this, renderer) ?: return
        ContextCompat.startForegroundService(this, request)
    }

    private companion object {
        const val SHIZUKU_PERMISSION_REQUEST = 10
    }
}
