package com.suitius.dlnacast.audio

import android.content.Context
import android.content.ContextWrapper
import android.content.AttributionSource
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.AudioTrack
import android.os.Looper
import android.os.Process
import android.os.SystemClock
import kotlin.math.PI
import kotlin.math.sin
import kotlin.math.sqrt
import kotlin.system.exitProcess

/** A shell-side compatibility probe that never starts recording. */
object ShizukuAudioPolicyDeviceCheck {
    @JvmStatic
    fun main(args: Array<String>) {
        require(args.isEmpty() || args.toList() == listOf("--speaker-mute"))
        try {
            verifyLoopback(args.isNotEmpty())
        } catch (error: Throwable) {
            error.printStackTrace(System.err)
            exitProcess(1)
        }
    }

    @Suppress("DEPRECATION")
    private fun verifyLoopback(checkSpeakerMute: Boolean) {
        check(Process.myUid() == Process.SHELL_UID) { "Run this probe as Android shell" }
        if (Looper.getMainLooper() == null) Looper.prepareMainLooper()
        val activityThreadClass = Class.forName("android.app.ActivityThread")
        val activityThread = activityThreadClass.getMethod("systemMain").invoke(null)
        val systemContext = activityThreadClass.getMethod("getSystemContext").invoke(activityThread) as Context
        val context = systemContext.createPackageContext("com.suitius.dlnacast", 0)
        val shell = context.createPackageContext("com.android.shell", 0)
        val attribution = shell.attributionSource
        val opPackage = Context::class.java.getMethod("getOpPackageName").invoke(shell)
        println("Shell context: package=${shell.packageName} opPackage=$opPackage " +
            "attributionUid=${attribution.uid} attributionPackage=${attribution.packageName}")

        val loopback = ShizukuAudioLoopback.open(context, Process.myUserHandle(), Process.SHELL_UID)
        val record = loopback.record
        loopback.use {
            check(record.state == AudioRecord.STATE_INITIALIZED)
            check(record.recordingState == AudioRecord.RECORDSTATE_STOPPED)
            if (checkSpeakerMute) verifySpeakerMute(context, shell, record)
        }
        check(record.state == AudioRecord.STATE_UNINITIALIZED)
        if (!checkSpeakerMute) println("Shizuku AudioPolicy recorder initialized and released without starting capture.")
    }

    private fun verifySpeakerMute(context: Context, shell: Context, recorder: AudioRecord) {
        val sdkAttribution = AttributionSource.Builder(Process.myUid()).setPackageName(shell.packageName).build()
        val audioContext = object : ContextWrapper(shell) {
            override fun getOpPackageName() = shell.packageName
            override fun getAttributionSource() = sdkAttribution
        }
        val frames = 4_800
        val signal = ShortArray(frames * 2) { sample -> (sin(2 * PI * (sample / 2) / 48) * 400).toInt().toShort() }
        val format = AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(48_000).setChannelMask(AudioFormat.CHANNEL_OUT_STEREO).build()
        val track = AudioTrack.Builder().setContext(audioContext)
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA)
                .setAllowedCapturePolicy(AudioAttributes.ALLOW_CAPTURE_BY_ALL).build())
            .setAudioFormat(format).setTransferMode(AudioTrack.MODE_STATIC)
            .setBufferSizeInBytes(signal.size * Short.SIZE_BYTES).build()
        try {
            check(track.write(signal, 0, signal.size) == signal.size)
            check(track.setLoopPoints(0, frames, -1) == AudioTrack.SUCCESS)
            recorder.startRecording()
            track.play()
            SystemClock.sleep(170)
            check(track.routedDevice?.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER) { "Test audio is not routed to the phone speaker" }
            val before = capturedRms(recorder)
            check(before > 5) { "No synthetic PCM reached the capture mix" }
            ShizukuSpeakerMute(context).use { mute ->
                check(mute.setMuted(true) && mute.isMuted())
                val after = capturedRms(recorder)
                check(after / before in 0.85..1.15) { "Phone speaker attenuation changed the captured amplitude" }
                check(!mute.setMuted(false))
                println("Speaker mute audio check passed: capture RMS before=$before and while muted=$after")
            }
        } finally {
            runCatching { track.stop() }
            track.release()
            runCatching { recorder.stop() }
        }
    }

    private fun capturedRms(recorder: AudioRecord): Double {
        val pcm = ShortArray(4_800 * 2)
        val deadline = SystemClock.elapsedRealtime() + 900
        var count = 0
        var squares = 0.0
        val startMeasurement = SystemClock.elapsedRealtime() + 120
        while (SystemClock.elapsedRealtime() < deadline && count < pcm.size) {
            val read = recorder.read(pcm, 0, pcm.size, AudioRecord.READ_NON_BLOCKING)
            check(read >= 0) { "AudioRecord did not return captured PCM" }
            if (SystemClock.elapsedRealtime() >= startMeasurement) {
                for (index in 0 until read) squares += pcm[index].toDouble() * pcm[index]
                count += read
            }
            SystemClock.sleep(5)
        }
        check(count >= pcm.size) { "Timed out receiving test audio" }
        return sqrt(squares / count)
    }
}
