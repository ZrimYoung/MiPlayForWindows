package com.suitius.dlnacast.service

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import com.suitius.dlnacast.MainActivity
import com.suitius.dlnacast.R
import com.suitius.dlnacast.audio.AudioChannelRoute
import com.suitius.dlnacast.audio.PcmAudioCapture
import com.suitius.dlnacast.audio.PlaybackAudioCapture
import com.suitius.dlnacast.audio.RootPlaybackAudioCapture
import com.suitius.dlnacast.audio.RoutedPcmOutput
import com.suitius.dlnacast.audio.ShizukuPlaybackAudioCapture
import com.suitius.dlnacast.miplay.MiPlayCastSession
import com.suitius.dlnacast.model.DlnaRenderer
import com.suitius.dlnacast.model.SpeakerIdentity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class CastService : Service() {
    private val scope = CoroutineScope(Job() + Dispatchers.Main.immediate)
    private var session: CastSession? = null
    private var castingJob: Job? = null
    private var castWakeLock: PowerManager.WakeLock? = null
    private var localSpeakerMuteError: String? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START, ACTION_ROOT_START, ACTION_SHIZUKU_START -> startCasting(intent)
            ACTION_STOP -> scope.launch { stopCasting(); stopSelf() }
            ACTION_PAUSE -> controlSession { it.pauseReceiver() }
            ACTION_RESUME -> controlSession { it.resumeReceiver() }
            ACTION_SET_LOCAL_SPEAKER_MUTE -> setLocalSpeakerMute(intent.getBooleanExtra(EXTRA_MUTE_LOCAL_SPEAKER, false))
            ACTION_SET_VOLUME -> {
                val volume = intent.getIntExtra(EXTRA_VOLUME, -1)
                val receiverIndex = intent.getIntExtra(EXTRA_RECEIVER_INDEX, NO_RECEIVER_INDEX)
                    .takeUnless { it == NO_RECEIVER_INDEX }
                val receiverIds = intent.getStringArrayListExtra(EXTRA_CONTROL_RENDERER_IDS)
                if (volume in 0..100) {
                    if (receiverIds.isNullOrEmpty()) controlSession { it.setReceiverVolume(volume, receiverIndex) }
                    else controlMiPlaySession { it.setGroupVolume(volume, receiverIds.toSet()) }
                }
            }
            ACTION_SET_CAPTURE_UID -> {
                val uid = intent.getIntExtra(EXTRA_CAPTURE_UID, NO_CAPTURE_UID)
                    .takeUnless { it == NO_CAPTURE_UID }
                controlSession { it.setCaptureUid(uid) }
            }
            ACTION_ADD_RENDERER -> {
                val renderers = intent.renderers()
                if (renderers.isNotEmpty()) controlMiPlaySession { it.addReceivers(renderers) }
            }
            ACTION_REMOVE_RENDERER -> {
                val rendererIds = intent.getStringArrayListExtra(EXTRA_CONTROL_RENDERER_IDS).orEmpty().toSet()
                if (rendererIds.isNotEmpty()) controlMiPlaySession { it.removeReceivers(rendererIds) }
            }
        }
        return START_NOT_STICKY
    }

    private fun startCasting(intent: Intent) {
        val renderers = intent.renderers()
        val renderer = renderers.firstOrNull()
        val protocol = intent.protocolOrDefault()
        val rootCapture = intent.getBooleanExtra(EXTRA_ROOT_CAPTURE, false)
        val shizukuCapture = intent.action == ACTION_SHIZUKU_START
        val muteLocalSpeaker = shizukuCapture && intent.getBooleanExtra(EXTRA_MUTE_LOCAL_SPEAKER, false)
        val requiresProjection = !rootCapture && !shizukuCapture
        val projectionData = intent.projectionDataOrNull()
        val captureUid = intent.getIntExtra(EXTRA_CAPTURE_UID, NO_CAPTURE_UID).takeUnless { it == NO_CAPTURE_UID }
        val channelRoutes = intent.channelRoutes(renderers.size)
        val channelRoute = channelRoutes.firstOrNull() ?: AudioChannelRoute.Stereo
        val playbackDelayMicroseconds = intent.getIntExtra(
            EXTRA_PLAYBACK_DELAY_MICROSECONDS,
            DEFAULT_PLAYBACK_DELAY_MICROSECONDS,
        ).coerceIn(0, MAXIMUM_PLAYBACK_DELAY_MICROSECONDS)
        val projectionResultCode = intent.getIntExtra(EXTRA_PROJECTION_RESULT_CODE, Int.MIN_VALUE)
        if (renderer == null || (requiresProjection && (projectionData == null || projectionResultCode == Int.MIN_VALUE))) {
            CastStateStore.update(CastStatus.Error("投送参数不完整，请返回应用后重试"))
            stopSelf()
            return
        }

        ServiceCompat.startForeground(
            this,
            NOTIFICATION_ID,
            buildNotification("正在连接 ${renderers.joinToString { it.name }}"),
            if (requiresProjection) {
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
            } else {
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
            },
        )
        acquireCastWakeLock()
        CastStateStore.update(
            CastStatus.Preparing(when {
                shizukuCapture -> "正在启动 Shizuku 音频捕获…"
                rootCapture -> "正在启动 Root 系统内录…"
                else -> "正在准备系统音频捕获…"
            }),
        )
        castingJob?.cancel()
        localSpeakerMuteError = null
        castingJob = scope.launch {
            var candidate: CastSession? = null
            try {
                session?.let { withContext(Dispatchers.IO) { it.close() } }
                val projection = if (requiresProjection) {
                    val projectionManager = getSystemService(MediaProjectionManager::class.java)
                    projectionManager.getMediaProjection(projectionResultCode, projectionData!!)
                        ?: error("Android 未返回可用的录屏授权")
                } else null
                candidate = withContext(Dispatchers.IO) {
                    val projectionStopped: () -> Unit = {
                        scope.launch {
                            CastStateStore.update(CastStatus.Error("系统已结束音频捕获授权"))
                            stopCasting(resetStatus = false)
                            stopSelf()
                        }
                        Unit
                    }
                    val createCapture: (List<RoutedPcmOutput>) -> PcmAudioCapture = { outputs ->
                        when {
                            shizukuCapture -> ShizukuPlaybackAudioCapture(applicationContext, outputs, captureUid)
                            rootCapture -> RootPlaybackAudioCapture(applicationContext, outputs)
                            else -> PlaybackAudioCapture(
                                applicationContext,
                                requireNotNull(projection),
                                outputs,
                                projectionStopped,
                                captureUid,
                            )
                        }
                    }
                    when (protocol) {
                        CastProtocol.DLNA -> ActiveCastSession(
                            renderer,
                            createCapture,
                            channelRoute,
                        )
                        CastProtocol.MIPLAY -> MiPlayCastSession(
                            renderers,
                            createCapture,
                            channelRoutes,
                            playbackDelayMicroseconds,
                        ) { phase ->
                            scope.launch {
                                if (session == null) CastStateStore.update(CastStatus.Preparing(phase))
                                updateNotification(phase)
                            }
                            Unit
                        }
                    }
                }
                val newSession = candidate
                withContext(Dispatchers.IO) { newSession.start() }
                if (muteLocalSpeaker) {
                    localSpeakerMuteError = try {
                        withContext(Dispatchers.IO) {
                            if (newSession.setLocalSpeakerMuted(true)) null else "手机扬声器未能静音"
                        }
                    } catch (cancelled: CancellationException) {
                        throw cancelled
                    } catch (error: Exception) {
                        error.message ?: "手机扬声器未能静音"
                    }
                }
                session = newSession
                val rendererNames = renderers.joinToString { it.name }
                updateNotification("正在投送到 $rendererNames")
                while (isActive && session === newSession) {
                    newSession.failureMessage?.let { error(it) }
                    Log.i(LOG_TAG, newSession.streamDetail)
                    val controls = newSession.receiverControls
                    val activeRenderers = (newSession as? MiPlayCastSession)?.activeRenderers ?: renderers
                    val speakerMuted = withContext(Dispatchers.IO) {
                        runCatching { newSession.localSpeakerMuteState() }.getOrNull()
                    }
                    CastStateStore.update(
                        CastStatus.Streaming(
                            activeRenderers.joinToString { it.name },
                            newSession.streamDetail,
                            newSession.supportsPlaybackControl,
                            newSession.receiverPaused,
                            newSession.receiverVolume,
                            controls,
                            newSession.supportsCaptureSwitch,
                            protocol,
                            activeRenderers,
                            channelRoutes.distinct().size > 1,
                            localSpeakerMuted = speakerMuted,
                            localSpeakerMuteError = localSpeakerMuteError,
                        ),
                    )
                    if (newSession.supportsPlaybackControl) {
                        updateNotification(
                            "正在投送到 ${activeRenderers.joinToString { it.name }} · ${if (newSession.receiverPaused) "已暂停" else "播放中"}",
                            newSession.receiverPaused,
                        )
                    }
                    delay(2_000)
                }
            } catch (cancelled: CancellationException) {
                withContext(NonCancellable + Dispatchers.IO) { candidate?.close() }
                if (session === candidate) session = null
                throw cancelled
            } catch (exception: Exception) {
                Log.e(LOG_TAG, "Cast startup failed", exception)
                withContext(Dispatchers.IO) { candidate?.close() }
                session = null
                CastStateStore.update(CastStatus.Error(exception.message ?: exception.javaClass.simpleName))
                ServiceCompat.stopForeground(this@CastService, ServiceCompat.STOP_FOREGROUND_REMOVE)
                releaseCastWakeLock()
                stopSelf()
            }
        }
    }

    private fun controlSession(operation: suspend (CastSession) -> Unit) {
        scope.launch {
            val active = session
            if (active == null) {
                CastStateStore.update(CastStatus.Error("当前没有正在运行的投送会话"))
                return@launch
            }
            runCatching { withContext(Dispatchers.IO) { operation(active) } }
                .onFailure { CastStateStore.update(CastStatus.Error(it.message ?: "MiPlay 控制失败")) }
        }
    }

    private fun setLocalSpeakerMute(muted: Boolean) {
        scope.launch {
            val active = session ?: return@launch
            val result = runCatching { withContext(Dispatchers.IO) { active.setLocalSpeakerMuted(muted) } }
            if (session !== active) return@launch
            val actual = result.getOrNull()
            localSpeakerMuteError = when {
                actual == muted -> null
                else -> result.exceptionOrNull()?.message ?: "手机扬声器静音状态未能更改"
            }
            if (actual == muted) CastPreferences(this@CastService).saveLocalSpeakerMute(muted)
            val state = CastStateStore.status.value as? CastStatus.Streaming ?: return@launch
            CastStateStore.update(state.copy(
                localSpeakerMuted = actual ?: state.localSpeakerMuted,
                localSpeakerMuteError = localSpeakerMuteError,
            ))
        }
    }

    private fun controlMiPlaySession(operation: suspend (MiPlayCastSession) -> Unit) {
        scope.launch {
            val active = session as? MiPlayCastSession
            if (active == null) {
                CastStateStore.update(CastStatus.Error("当前没有正在运行的 MiPlay 会话"))
                return@launch
            }
            runCatching { withContext(Dispatchers.IO) { operation(active) } }
                .onFailure { CastStateStore.update(CastStatus.Error(it.message ?: "MiPlay 设备切换失败")) }
        }
    }

    private suspend fun stopCasting(resetStatus: Boolean = true) {
        val job = castingJob
        castingJob = null
        job?.cancelAndJoin()
        val active = session
        session = null
        withContext(Dispatchers.IO) { active?.close() }
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        releaseCastWakeLock()
        if (resetStatus) CastStateStore.update(CastStatus.Idle)
    }

    private fun acquireCastWakeLock() {
        val active = castWakeLock ?: getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "$packageName:cast")
            .apply { setReferenceCounted(false) }
            .also { castWakeLock = it }
        if (!active.isHeld) active.acquire()
    }

    private fun releaseCastWakeLock() {
        castWakeLock?.takeIf { it.isHeld }?.release()
    }

    private fun buildNotification(message: String, receiverPaused: Boolean = false) =
        NotificationCompat.Builder(this, CHANNEL_ID)
        .setSmallIcon(R.drawable.ic_cast)
        .setContentTitle("MiPlay Cast")
        .setContentText(message)
        .setContentIntent(
            PendingIntent.getActivity(
                this,
                0,
                Intent(this, MainActivity::class.java),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            ),
        )
        .addAction(
            R.drawable.ic_cast,
            if (receiverPaused) "继续" else "暂停",
            PendingIntent.getService(
                this,
                if (receiverPaused) 3 else 2,
                if (receiverPaused) resumeIntent(this) else pauseIntent(this),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            ),
        )
        .addAction(
            R.drawable.ic_stop,
            "停止",
            PendingIntent.getService(
                this,
                1,
                stopIntent(this),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            ),
        )
        .setOngoing(true)
        .setCategory(NotificationCompat.CATEGORY_SERVICE)
        .build()

    private fun updateNotification(message: String, receiverPaused: Boolean = false) {
        getSystemService(NotificationManager::class.java)
            .notify(NOTIFICATION_ID, buildNotification(message, receiverPaused))
    }

    private fun createNotificationChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            "音频投送",
            NotificationManager.IMPORTANCE_LOW,
        ).apply { description = "显示正在运行的 MiPlay/DLNA 音频投送" }
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    override fun onDestroy() {
        val active = session
        session = null
        scope.cancel()
        if (active != null) CoroutineScope(Dispatchers.IO).launch { active.close() }
        releaseCastWakeLock()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun Intent.renderers(): List<DlnaRenderer> {
        val ids = getStringArrayListExtra(EXTRA_RENDERER_ID).orEmpty()
        val names = getStringArrayListExtra(EXTRA_RENDERER_NAME).orEmpty()
        val descriptions = getStringArrayListExtra(EXTRA_RENDERER_DESCRIPTION_URL).orEmpty()
        val hosts = getStringArrayListExtra(EXTRA_RENDERER_HOST).orEmpty()
        val transports = getStringArrayListExtra(EXTRA_RENDERER_AV_TRANSPORT).orEmpty()
        val renderingControls = getStringArrayListExtra(EXTRA_RENDERER_RENDERING_CONTROL).orEmpty()
        val connectionManagers = getStringArrayListExtra(EXTRA_RENDERER_CONNECTION_MANAGER).orEmpty()
        val groupIds = getStringArrayListExtra(EXTRA_RENDERER_GROUP_ID).orEmpty()
        val groupNames = getStringArrayListExtra(EXTRA_RENDERER_GROUP_NAME).orEmpty()
        val channels = getStringArrayListExtra(EXTRA_RENDERER_CHANNEL).orEmpty()
        val controlPorts = getIntArrayExtra(EXTRA_RENDERER_CONTROL_PORT)
        val secureMiPlay = getBooleanArrayExtra(EXTRA_RENDERER_SECURE_MIPLAY)
        if (listOf(names, descriptions, hosts, transports, renderingControls, connectionManagers).any { it.size != ids.size }) {
            return emptyList()
        }
        return ids.indices.map { index ->
            DlnaRenderer(
                ids[index],
                names[index],
                descriptions[index],
                hosts[index],
                transports[index],
                renderingControls[index].ifEmpty { null },
                connectionManagers[index].ifEmpty { null },
                supportsMiPlay = protocolOrDefault() == CastProtocol.MIPLAY,
                miPlayControlPort = controlPorts?.getOrNull(index)?.takeIf { it in 1..65535 },
                usesSecureMiPlay = secureMiPlay?.getOrNull(index) == true,
                miPlayIdentity = groupIds.getOrNull(index)?.takeIf(String::isNotBlank)?.let { group ->
                    SpeakerIdentity(name = names[index], groupId = group,
                        groupName = groupNames.getOrNull(index).orEmpty(), channel = channels.getOrNull(index).orEmpty(),
                        hasGroupInfo = true)
                },
            )
        }
    }

    @Suppress("DEPRECATION")
    private fun Intent.projectionDataOrNull(): Intent? = if (Build.VERSION.SDK_INT >= 33) {
        getParcelableExtra(EXTRA_PROJECTION_DATA, Intent::class.java)
    } else {
        getParcelableExtra(EXTRA_PROJECTION_DATA)
    }

    companion object {
        private const val ACTION_START = "com.suitius.dlnacast.action.START"
        private const val ACTION_ROOT_START = "com.suitius.dlnacast.action.ROOT_START"
        private const val ACTION_SHIZUKU_START = "com.suitius.dlnacast.action.SHIZUKU_START"
        private const val ACTION_SET_LOCAL_SPEAKER_MUTE = "com.suitius.dlnacast.action.SET_LOCAL_SPEAKER_MUTE"
        private const val ACTION_STOP = "com.suitius.dlnacast.action.STOP"
        private const val ACTION_PAUSE = "com.suitius.dlnacast.action.PAUSE"
        private const val ACTION_RESUME = "com.suitius.dlnacast.action.RESUME"
        private const val ACTION_SET_VOLUME = "com.suitius.dlnacast.action.SET_VOLUME"
        private const val ACTION_SET_CAPTURE_UID = "com.suitius.dlnacast.action.SET_CAPTURE_UID"
        private const val ACTION_ADD_RENDERER = "com.suitius.dlnacast.action.ADD_RENDERER"
        private const val ACTION_REMOVE_RENDERER = "com.suitius.dlnacast.action.REMOVE_RENDERER"
        private const val EXTRA_VOLUME = "receiver_volume"
        private const val EXTRA_RECEIVER_INDEX = "receiver_index"
        private const val EXTRA_CAPTURE_UID = "capture_uid"
        private const val EXTRA_MUTE_LOCAL_SPEAKER = "mute_local_speaker"
        private const val EXTRA_CHANNEL_ROUTE = "channel_route"
        private const val EXTRA_PLAYBACK_DELAY_MICROSECONDS = "playback_delay_microseconds"
        private const val NO_CAPTURE_UID = -1
        private const val NO_RECEIVER_INDEX = -1
        private const val MAX_MIPLAY_RECEIVERS = 63
        private const val MAX_DLNA_RECEIVERS = 1
        private const val DEFAULT_PLAYBACK_DELAY_MICROSECONDS = 1_000_000
        private const val MAXIMUM_PLAYBACK_DELAY_MICROSECONDS = 2_000_000
        private const val EXTRA_PROJECTION_RESULT_CODE = "projection_result_code"
        private const val EXTRA_PROJECTION_DATA = "projection_data"
        private const val EXTRA_ROOT_CAPTURE = "root_capture"
        private const val EXTRA_RENDERER_ID = "renderer_id"
        private const val EXTRA_RENDERER_NAME = "renderer_name"
        private const val EXTRA_RENDERER_DESCRIPTION_URL = "renderer_description_url"
        private const val EXTRA_RENDERER_HOST = "renderer_host"
        private const val EXTRA_RENDERER_AV_TRANSPORT = "renderer_av_transport"
        private const val EXTRA_RENDERER_RENDERING_CONTROL = "renderer_rendering_control"
        private const val EXTRA_RENDERER_CONNECTION_MANAGER = "renderer_connection_manager"
        private const val EXTRA_PROTOCOL = "cast_protocol"
        private const val EXTRA_RENDERER_GROUP_ID = "renderer_group_id"
        private const val EXTRA_RENDERER_GROUP_NAME = "renderer_group_name"
        private const val EXTRA_RENDERER_CHANNEL = "renderer_channel"
        private const val EXTRA_RENDERER_CONTROL_PORT = "renderer_control_port"
        private const val EXTRA_RENDERER_SECURE_MIPLAY = "renderer_secure_miplay"
        private const val EXTRA_CONTROL_RENDERER_IDS = "control_renderer_ids"
        private const val CHANNEL_ID = "cast-session"
        private const val NOTIFICATION_ID = 1001
        private const val LOG_TAG = "DLNACast.Cast"

        fun startIntent(
            context: Context,
            renderers: List<DlnaRenderer>,
            protocol: CastProtocol,
            projectionResultCode: Int,
            projectionData: Intent,
            captureUid: Int? = null,
            channelRoutes: List<AudioChannelRoute> = List(renderers.size) { AudioChannelRoute.Stereo },
            playbackDelayMicroseconds: Int = DEFAULT_PLAYBACK_DELAY_MICROSECONDS,
        ) = captureStartIntent(
            context, ACTION_START, renderers, protocol, captureUid, channelRoutes, playbackDelayMicroseconds,
        ).apply {
            putExtra(EXTRA_PROJECTION_RESULT_CODE, projectionResultCode)
            putExtra(EXTRA_PROJECTION_DATA, projectionData)
        }

        fun rootStartIntent(
            context: Context,
            renderers: List<DlnaRenderer>,
            protocol: CastProtocol = CastProtocol.MIPLAY,
            channelRoutes: List<AudioChannelRoute> = List(renderers.size) { AudioChannelRoute.Stereo },
            playbackDelayMicroseconds: Int = DEFAULT_PLAYBACK_DELAY_MICROSECONDS,
        ) = captureStartIntent(
            context, ACTION_ROOT_START, renderers, protocol, null, channelRoutes, playbackDelayMicroseconds,
        ).apply { putExtra(EXTRA_ROOT_CAPTURE, true) }

        fun shizukuStartIntent(
            context: Context,
            renderers: List<DlnaRenderer>,
            protocol: CastProtocol,
            captureUid: Int?,
            channelRoutes: List<AudioChannelRoute>,
            playbackDelayMicroseconds: Int,
            muteLocalSpeaker: Boolean,
        ) = captureStartIntent(
            context, ACTION_SHIZUKU_START, renderers, protocol, captureUid, channelRoutes, playbackDelayMicroseconds,
        ).apply { putExtra(EXTRA_MUTE_LOCAL_SPEAKER, muteLocalSpeaker) }

        fun setLocalSpeakerMuteIntent(context: Context, muted: Boolean) = Intent(context, CastService::class.java).apply {
            action = ACTION_SET_LOCAL_SPEAKER_MUTE
            putExtra(EXTRA_MUTE_LOCAL_SPEAKER, muted)
        }

        private fun captureStartIntent(
            context: Context,
            startAction: String,
            renderers: List<DlnaRenderer>,
            protocol: CastProtocol,
            captureUid: Int?,
            channelRoutes: List<AudioChannelRoute>,
            playbackDelayMicroseconds: Int,
        ) = Intent(context, CastService::class.java).apply {
            require(renderers.isNotEmpty()) { "至少选择一台音箱" }
            require(renderers.size <= if (protocol == CastProtocol.MIPLAY) MAX_MIPLAY_RECEIVERS else MAX_DLNA_RECEIVERS) {
                "${protocol.name} 超过可选音箱数量上限"
            }
            require(channelRoutes.size == renderers.size) { "音箱数量和声道路由数量不一致" }
            action = startAction
            putRenderers(renderers)
            putExtra(EXTRA_PROTOCOL, protocol.name)
            captureUid?.let { putExtra(EXTRA_CAPTURE_UID, it) }
            putStringArrayListExtra(EXTRA_CHANNEL_ROUTE, ArrayList(channelRoutes.map { it.name }))
            putExtra(
                EXTRA_PLAYBACK_DELAY_MICROSECONDS,
                playbackDelayMicroseconds.coerceIn(0, MAXIMUM_PLAYBACK_DELAY_MICROSECONDS),
            )
        }

        private fun Intent.putRenderers(renderers: List<DlnaRenderer>) {
            putStringArrayListExtra(EXTRA_RENDERER_ID, ArrayList(renderers.map { it.id }))
            putStringArrayListExtra(EXTRA_RENDERER_NAME, ArrayList(renderers.map { it.name }))
            putStringArrayListExtra(EXTRA_RENDERER_DESCRIPTION_URL, ArrayList(renderers.map { it.descriptionUrl }))
            putStringArrayListExtra(EXTRA_RENDERER_HOST, ArrayList(renderers.map { it.host }))
            putStringArrayListExtra(EXTRA_RENDERER_AV_TRANSPORT, ArrayList(renderers.map { it.avTransportControlUrl }))
            putStringArrayListExtra(
                EXTRA_RENDERER_RENDERING_CONTROL,
                ArrayList(renderers.map { it.renderingControlUrl.orEmpty() }),
            )
            putStringArrayListExtra(
                EXTRA_RENDERER_CONNECTION_MANAGER,
                ArrayList(renderers.map { it.connectionManagerControlUrl.orEmpty() }),
            )
            putStringArrayListExtra(EXTRA_RENDERER_GROUP_ID, ArrayList(renderers.map { it.miPlayIdentity?.groupId.orEmpty() }))
            putStringArrayListExtra(EXTRA_RENDERER_GROUP_NAME, ArrayList(renderers.map { it.miPlayIdentity?.groupName.orEmpty() }))
            putStringArrayListExtra(EXTRA_RENDERER_CHANNEL, ArrayList(renderers.map { it.miPlayIdentity?.channel.orEmpty() }))
            putExtra(EXTRA_RENDERER_CONTROL_PORT, IntArray(renderers.size) { renderers[it].miPlayControlPort ?: 0 })
            putExtra(EXTRA_RENDERER_SECURE_MIPLAY, BooleanArray(renderers.size) { renderers[it].usesSecureMiPlay })
        }

        fun stopIntent(context: Context) = Intent(context, CastService::class.java).apply { action = ACTION_STOP }

        fun pauseIntent(context: Context) = Intent(context, CastService::class.java).apply { action = ACTION_PAUSE }

        fun resumeIntent(context: Context) = Intent(context, CastService::class.java).apply { action = ACTION_RESUME }

        fun setVolumeIntent(context: Context, volume: Int, receiverIndex: Int? = null) =
            Intent(context, CastService::class.java).apply {
            action = ACTION_SET_VOLUME
            putExtra(EXTRA_VOLUME, volume.coerceIn(0, 100))
            receiverIndex?.let { putExtra(EXTRA_RECEIVER_INDEX, it) }
        }

        fun setCaptureUidIntent(context: Context, captureUid: Int?) =
            Intent(context, CastService::class.java).apply {
                action = ACTION_SET_CAPTURE_UID
                putExtra(EXTRA_CAPTURE_UID, captureUid ?: NO_CAPTURE_UID)
            }

        fun setGroupVolumeIntent(context: Context, volume: Int, rendererIds: List<String>) =
            setVolumeIntent(context, volume).putStringArrayListExtra(EXTRA_CONTROL_RENDERER_IDS, ArrayList(rendererIds))

        fun addRendererIntent(context: Context, renderer: DlnaRenderer) = addRenderersIntent(context, listOf(renderer))

        internal fun addRenderersIntent(context: Context, renderers: List<DlnaRenderer>) =
            Intent(context, CastService::class.java).apply {
                action = ACTION_ADD_RENDERER
                putRenderers(renderers)
            }

        fun removeRendererIntent(context: Context, rendererId: String) = removeRenderersIntent(context, listOf(rendererId))

        internal fun removeRenderersIntent(context: Context, rendererIds: List<String>) =
            Intent(context, CastService::class.java).apply {
                action = ACTION_REMOVE_RENDERER
                putStringArrayListExtra(EXTRA_CONTROL_RENDERER_IDS, ArrayList(rendererIds))
            }
    }

    private fun Intent.protocolOrDefault(): CastProtocol =
        getStringExtra(EXTRA_PROTOCOL)?.let { value ->
            CastProtocol.entries.firstOrNull { it.name == value }
        } ?: CastProtocol.MIPLAY

    private fun Intent.channelRoutes(rendererCount: Int): List<AudioChannelRoute> {
        val stored = getStringArrayListExtra(EXTRA_CHANNEL_ROUTE).orEmpty()
            .mapNotNull { value -> AudioChannelRoute.entries.firstOrNull { it.name == value } }
        return stored.takeIf { it.size == rendererCount }
            ?: List(rendererCount) { AudioChannelRoute.Stereo }
    }

}
