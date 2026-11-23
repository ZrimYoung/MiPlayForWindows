package com.suitius.dlnacast.audio

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioManager
import android.os.Binder
import android.os.Process
import java.lang.reflect.InvocationTargetException
import java.lang.reflect.Method

/** Temporarily attenuates only the media stream on the phone's built-in speaker. */
internal class ShizukuSpeakerMute(private val volume: SpeakerVolume) : AutoCloseable {
    constructor(context: Context) : this(AndroidSpeakerVolume(context))

    private var lease: VolumeLease? = null

    @Synchronized
    fun setMuted(muted: Boolean): Boolean {
        if (!muted) {
            restore()
            return false
        }
        if (isMuted()) return true

        val original = VolumeLease(volume.index(), volume.isStreamMuted())
        check(!original.streamMuted || original.speakerIndex == 0) { "手机媒体已由系统静音" }
        lease = original
        if (original.speakerIndex != 0) {
            try {
                volume.setIndex(0)
                check(isMuted()) { "系统未将手机扬声器静音" }
            } catch (failure: Exception) {
                runCatching { restore() }.onFailure(failure::addSuppressed)
                throw failure
            }
        }
        return true
    }

    @Synchronized
    fun isMuted(): Boolean {
        val active = lease ?: return false
        if (volume.index() == 0 && volume.isStreamMuted() == active.streamMuted) return true
        lease = null
        return false
    }

    @Synchronized
    override fun close() = restore()

    private fun restore() {
        val original = lease ?: return
        if (isMuted() && original.speakerIndex != 0) volume.setIndex(original.speakerIndex)
        lease = null
    }

    private data class VolumeLease(val speakerIndex: Int, val streamMuted: Boolean)

    internal interface SpeakerVolume {
        fun index(): Int
        fun isStreamMuted(): Boolean
        fun setIndex(index: Int)
    }

    private class AndroidSpeakerVolume(private val context: Context) : SpeakerVolume {
        private val methods by lazy { SpeakerVolumeMethods.fromPlatform() }
        private val manager by lazy { checkNotNull(context.getSystemService(AudioManager::class.java)) }

        override fun index(): Int {
            val index = invoke(methods.getIndex, AudioManager.STREAM_MUSIC, methods.speaker)
            check(index >= 0) { "无法读取手机扬声器的媒体音量（$index）" }
            return index
        }

        override fun isStreamMuted(): Boolean = manager.isStreamMute(AudioManager.STREAM_MUSIC)

        override fun setIndex(index: Int) {
            check(Process.myUid() == Process.SHELL_UID && Binder.getCallingUid() == Process.myUid() &&
                context.checkSelfPermission(Manifest.permission.MODIFY_AUDIO_SETTINGS) == PackageManager.PERMISSION_GRANTED
            ) { "Shizuku 没有调整手机扬声器音量的权限" }
            val status = if (methods.hasMuteParameter) {
                // The native mute bit can be shared by the whole volume group. Only set this device's index.
                invoke(methods.setIndex, AudioManager.STREAM_MUSIC, index, false, methods.speaker)
            } else {
                invoke(methods.setIndex, AudioManager.STREAM_MUSIC, index, methods.speaker)
            }
            check(status == 0) { "系统拒绝调整手机扬声器的媒体音量（$status）" }
        }

        private fun invoke(method: Method, vararg arguments: Any): Int = try {
            method.invoke(null, *arguments) as Int
        } catch (error: InvocationTargetException) {
            throw IllegalStateException("系统无法独立调整手机扬声器的媒体音量", error.targetException)
        } catch (error: ReflectiveOperationException) {
            throw IllegalStateException("此系统不支持独立调整手机扬声器的媒体音量", error)
        }
    }

    private data class SpeakerVolumeMethods(
        val speaker: Int,
        val getIndex: Method,
        val setIndex: Method,
        val hasMuteParameter: Boolean,
    ) {
        companion object {
            fun fromPlatform(): SpeakerVolumeMethods = try {
                val audioSystem = Class.forName("android.media.AudioSystem")
                val type = Integer.TYPE
                val speaker = audioSystem.getField("DEVICE_OUT_SPEAKER").getInt(null)
                val getIndex = audioSystem.getMethod("getStreamVolumeIndex", type, type)
                val setIndex = try {
                    audioSystem.getMethod("setStreamVolumeIndexAS", type, type, Boolean::class.javaPrimitiveType, type)
                } catch (_: NoSuchMethodException) {
                    audioSystem.getMethod("setStreamVolumeIndexAS", type, type, type)
                }
                SpeakerVolumeMethods(speaker, getIndex, setIndex, setIndex.parameterCount == 4)
            } catch (error: ReflectiveOperationException) {
                throw IllegalStateException("此系统不支持独立调整手机扬声器的媒体音量", error)
            }
        }
    }
}
