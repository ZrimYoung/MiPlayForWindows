package com.suitius.dlnacast.audio

import android.content.ComponentName
import android.content.Context
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.Binder
import android.os.Build
import android.os.IBinder
import android.os.ParcelFileDescriptor
import android.os.Process
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import rikka.shizuku.Shizuku

class ShizukuPlaybackAudioCapture(
    context: Context,
    private val outputs: List<RoutedPcmOutput>,
    initialMatchingUid: Int?,
) : PcmAudioCapture {
    init {
        require(outputs.isNotEmpty()) { "至少需要一个 PCM 捕获输出" }
    }

    private val scope = CoroutineScope(Job() + Dispatchers.IO)
    private val closed = AtomicBoolean(false)
    private val lifetime = Binder()
    private val connectionResult = CompletableFuture<IShizukuAudioCapture>()
    private val serviceArgs = Shizuku.UserServiceArgs(ComponentName(context, ShizukuAudioCaptureService::class.java))
        .processNameSuffix("audio_capture").daemon(false).version(2)
    private var bound = false
    private var remote: IShizukuAudioCapture? = null
    private var reader: Job? = null
    private var activeUid = initialMatchingUid
    private var desiredSpeakerMute = false
    @Volatile private var input: ParcelFileDescriptor.AutoCloseInputStream? = null

    override val supportsUidFilter = true
    @Volatile override var failureMessage: String? = null
        private set

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName, binder: IBinder) {
            connectionResult.complete(IShizukuAudioCapture.Stub.asInterface(binder))
        }

        override fun onServiceDisconnected(name: ComponentName) {
            val message = "Shizuku 音频服务已断开"
            connectionResult.completeExceptionally(IllegalStateException(message))
            if (!closed.get()) failureMessage = message
        }
    }

    @Synchronized
    override fun start() {
        check(!bound && !closed.get()) { "Shizuku 音频捕获已经启动或关闭" }
        check(Build.VERSION.SDK_INT >= MINIMUM_SDK) { "Shizuku 音频需要 Android 13 或更新版本" }
        check(Shizuku.pingBinder()) { "请先启动 Shizuku" }
        check(Shizuku.getVersion() >= 13) { "Shizuku 音频需要 Shizuku 13 或更新版本" }
        check(Shizuku.getUid() == Process.SHELL_UID) { "请通过 ADB 启动 Shizuku 音频服务" }
        check(Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED) { "请在 Shizuku 中授权 MiPlay Cast" }
        try {
            bound = true
            Shizuku.bindUserService(serviceArgs, connection)
            remote = connectionResult.get(8, TimeUnit.SECONDS)
            startReader(activeUid)
        } catch (error: Exception) {
            close()
            when (error) {
                is TimeoutException -> throw IllegalStateException("连接 Shizuku 音频服务超时", error)
                is ExecutionException -> throw IllegalStateException(error.cause?.message ?: "Shizuku 音频无法连接", error.cause)
                else -> throw error
            }
        }
    }

    @Synchronized
    internal fun readTrackMetadata(): CapturedTrackMetadata? =
        if (closed.get()) null else CapturedTrackMetadata.fromBundle(remote?.readTrackMetadata())

    @Synchronized
    internal fun isSpeakerMuted(): Boolean = remote?.isSpeakerMuted() ?: false

    @Synchronized
    internal fun setSpeakerMuted(muted: Boolean): Boolean {
        check(!closed.get()) { "Shizuku 音频捕获已经关闭" }
        val actual = checkNotNull(remote).setSpeakerMuted(muted)
        desiredSpeakerMute = actual
        return actual
    }

    override suspend fun setMatchingUid(matchingUid: Int?) = withContext(Dispatchers.IO) {
        synchronized(this@ShizukuPlaybackAudioCapture) {
            check(!closed.get()) { "Shizuku 音频捕获已经关闭" }
            if (matchingUid == activeUid) return@synchronized
            try {
                stopReader()
                outputs.forEach { it.buffer.discardPendingAudio() }
                startReader(matchingUid)
                activeUid = matchingUid
            } catch (error: Exception) {
                failureMessage = error.message ?: "Shizuku 音源切换失败"
                throw error
            }
        }
    }

    private fun startReader(matchingUid: Int?) {
        val descriptor = checkNotNull(remote).startCapture(matchingUid ?: ALL_MEDIA_UID, lifetime)
        val stream = ParcelFileDescriptor.AutoCloseInputStream(descriptor)
        input = stream
        if (desiredSpeakerMute) {
            check(setSpeakerMuted(true)) { "手机扬声器未能静音" }
        }
        reader = scope.launch {
            try {
                stream.use {
                    val frame = ByteArray(PcmFrameBuffer.FRAME_BYTES)
                    while (isActive && input === stream) {
                        var offset = 0
                        while (offset < frame.size) {
                            val count = stream.read(frame, offset, frame.size - offset)
                            if (count < 0) break
                            offset += count
                        }
                        if (!isActive || input !== stream) break
                        if (offset != frame.size) {
                            descriptor.checkError()
                            error("Shizuku 音频数据流已中断")
                        }
                        val captured = frame.copyOf()
                        outputs.forEach { it.buffer.offer(it.route.applyToStereoPcm16(captured)) }
                    }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                if (input === stream) failureMessage = error.message ?: "Shizuku 音频数据流已中断"
            }
        }
    }

    private fun stopReader() {
        val previous = input
        input = null
        reader?.cancel()
        try {
            remote?.stopCapture()
        } finally {
            runCatching { previous?.close() }
            runBlocking { withTimeout(2_000) { reader?.cancelAndJoin() } }
            reader = null
        }
    }

    @Synchronized
    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        runCatching { stopReader() }
        if (bound) runCatching { Shizuku.unbindUserService(serviceArgs, connection, true) }
        scope.cancel()
    }

    companion object {
        const val MINIMUM_SDK = Build.VERSION_CODES.TIRAMISU
    }
}
