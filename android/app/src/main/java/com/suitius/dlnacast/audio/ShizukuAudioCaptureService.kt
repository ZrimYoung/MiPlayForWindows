package com.suitius.dlnacast.audio

import android.content.Context
import android.media.AudioRecord
import android.os.Binder
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import android.os.ParcelFileDescriptor
import android.os.Process
import android.os.UserHandle
import androidx.annotation.Keep
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.system.exitProcess

/** Shizuku instantiates this binder in its separate, user-authorized service process. */
@Keep
class ShizukuAudioCaptureService(private val context: Context) : IShizukuAudioCapture.Stub() {
    private val appUid = context.applicationInfo.uid
    private val appUser = UserHandle.getUserHandleForUid(appUid)
    private val trackReader by lazy { ShizukuTrackReader(context) }
    private var active: Capture? = null
    private var speakerMute: ShizukuSpeakerMute? = null

    @Synchronized
    override fun startCapture(matchingUid: Int, client: IBinder): ParcelFileDescriptor {
        requireAppCaller()
        check(Build.VERSION.SDK_INT >= ShizukuPlaybackAudioCapture.MINIMUM_SDK) { "Shizuku 音频需要 Android 13 或更新版本" }
        check(Process.myUid() == Process.SHELL_UID) { "需要通过 ADB 启动 Shizuku" }
        if (matchingUid != ALL_MEDIA_UID) {
            require(Process.isApplicationUid(matchingUid) && UserHandle.getUserHandleForUid(matchingUid) == appUser) {
                "Shizuku 模式只能选择同一用户下的应用音频"
            }
        }
        stopActive()

        val (readEnd, writeEnd) = ParcelFileDescriptor.createReliablePipe()
        val capture = Capture(matchingUid, client, writeEnd)
        return try {
            client.linkToDeath(capture.clientDeath, 0)
            active = capture
            capture.thread.start()
            capture.awaitStart()
            readEnd
        } catch (error: Exception) {
            runCatching { readEnd.close() }
            if (active === capture) runCatching { stopActive() } else runCatching { writeEnd.close() }
            throw IllegalStateException(error.message ?: "Shizuku 音频无法启动", error)
        }
    }

    @Synchronized
    override fun stopCapture() {
        requireAppCaller()
        stopActive()
    }

    @Synchronized
    override fun readTrackMetadata(): Bundle? {
        requireAppCaller()
        val capture = active ?: return null
        return withShellIdentity { trackReader.read(capture.matchingUid) }
    }

    @Synchronized
    override fun setSpeakerMuted(muted: Boolean): Boolean {
        requireAppCaller()
        check(active != null) { "请先启动 Shizuku 音频捕获" }
        return withShellIdentity {
            val lease = speakerMute ?: if (!muted) return@withShellIdentity false
                else ShizukuSpeakerMute(context).also { speakerMute = it }
            lease.setMuted(muted)
        }
    }

    @Synchronized
    override fun isSpeakerMuted(): Boolean {
        requireAppCaller()
        return withShellIdentity { speakerMute?.isMuted() ?: false }
    }

    @Synchronized
    override fun destroy() {
        check(Binder.getCallingUid() == Process.myUid()) { "只有 Shizuku 可以结束音频服务进程" }
        runCatching { stopActive() }
        exitProcess(0)
    }

    private fun requireAppCaller() {
        if (Binder.getCallingUid() != appUid) throw SecurityException("该音频服务只接受 MiPlay Cast 调用")
    }

    private fun stopActive() {
        try {
            val capture = active ?: return
            capture.stop()
            active = null
            capture.client.unlinkToDeath(capture.clientDeath, 0)
        } finally {
            withShellIdentity { speakerMute?.close() }
            speakerMute = null
        }
    }

    private inline fun <T> withShellIdentity(block: () -> T): T {
        val token = Binder.clearCallingIdentity()
        try { return block() } finally { Binder.restoreCallingIdentity(token) }
    }

    private inner class Capture(
        val matchingUid: Int,
        val client: IBinder,
        private val writeEnd: ParcelFileDescriptor,
    ) {
        private val stopping = AtomicBoolean(false)
        private val ready = CompletableFuture<Unit>()
        @Volatile private var record: AudioRecord? = null
        val thread = Thread(::transfer, "MiPlay-Shizuku-PCM")
        val clientDeath = IBinder.DeathRecipient {
            synchronized(this@ShizukuAudioCaptureService) {
                if (active === this) runCatching { stopActive() }
            }
        }

        fun awaitStart() {
            try {
                ready.get(8, TimeUnit.SECONDS)
            } catch (error: ExecutionException) {
                throw IllegalStateException(error.cause?.message ?: "Shizuku 音频采集启动失败", error.cause)
            } catch (error: TimeoutException) {
                throw IllegalStateException("Shizuku 音频采集启动超时", error)
            }
        }

        fun stop() {
            stopping.set(true)
            runCatching { writeEnd.close() }
            runCatching { record?.stop() }
            thread.join(2_000)
            check(!thread.isAlive) { "上一条 Shizuku 音频流尚未停止" }
        }

        private fun transfer() {
            try {
                // Running on this thread retains the service identity outside the app's Binder call.
                ShizukuAudioLoopback.open(context, appUser, matchingUid).use { loopback ->
                    val audio = loopback.record
                    record = audio
                    if (stopping.get()) return
                    runCatching { Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO) }
                    audio.startRecording()
                    check(audio.recordingState == AudioRecord.RECORDSTATE_RECORDING) { "系统未启动 Shizuku 音频采集" }
                    ready.complete(Unit)
                    val output = ParcelFileDescriptor.AutoCloseOutputStream(writeEnd)
                    val frame = ByteArray(PcmFrameBuffer.FRAME_BYTES)
                    while (!stopping.get()) {
                        val count = audio.read(frame, 0, frame.size, AudioRecord.READ_BLOCKING)
                        if (count > 0 && !stopping.get()) output.write(frame, 0, count)
                        if (count < 0 && !stopping.get()) error("系统中断了 Shizuku 音频采集（$count）")
                    }
                }
            } catch (error: Exception) {
                ready.completeExceptionally(error)
                if (!stopping.get()) runCatching { writeEnd.closeWithError(error.message ?: "Shizuku 音频中断") }
            } finally {
                record = null
                runCatching { writeEnd.close() }
            }
        }
    }
}
