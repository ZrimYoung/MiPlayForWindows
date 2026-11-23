package com.suitius.dlnacast.service

import android.content.Context
import com.suitius.dlnacast.audio.AudioChannelRoute
import androidx.core.content.edit

class CastPreferences(context: Context) {
    private val preferences = context.applicationContext
        .getSharedPreferences("cast_preferences", Context.MODE_PRIVATE)

    val protocol: CastProtocol
        get() = preferences.getString(KEY_PROTOCOL, null)?.let { stored ->
            CastProtocol.entries.firstOrNull { it.name == stored }
        } ?: CastProtocol.MIPLAY

    val captureUid: Int?
        get() = preferences.getInt(KEY_CAPTURE_UID, NO_UID).takeUnless { it == NO_UID }

    val capturePackageName: String?
        get() = preferences.getString(KEY_CAPTURE_PACKAGE, null)

    val channelRoute: AudioChannelRoute
        get() = preferences.getString(KEY_CHANNEL_ROUTE, null)?.let { stored ->
            AudioChannelRoute.entries.firstOrNull { it.name == stored }
        } ?: AudioChannelRoute.Stereo

    val playbackDelayMilliseconds: Float
        get() = preferences.getInt(KEY_PLAYBACK_DELAY_MS, 1_000).coerceIn(0, 2_000).toFloat()

    val rendererIds: List<String>
        get() = preferences.getString(KEY_RENDERER_IDS, null)
            ?.split(RENDERER_SEPARATOR)
            ?.filter(String::isNotBlank)
            ?.take(2)
            .orEmpty()

    val stereoSplit: Boolean
        get() = preferences.getBoolean(KEY_STEREO_SPLIT, false)

    val useShizukuCapture: Boolean
        get() = preferences.getBoolean(KEY_SHIZUKU_CAPTURE, false)

    val muteLocalSpeaker: Boolean
        get() = preferences.getBoolean(KEY_MUTE_LOCAL_SPEAKER, false)

    fun saveLocalSpeakerMute(muted: Boolean) {
        preferences.edit { putBoolean(KEY_MUTE_LOCAL_SPEAKER, muted) }
    }

    fun saveRendererIds(rendererIds: List<String>) {
        preferences.edit {
            putString(KEY_RENDERER_IDS, rendererIds.take(2).joinToString(RENDERER_SEPARATOR))
        }
    }

    fun saveOptions(
        protocol: CastProtocol,
        captureUid: Int?,
        capturePackageName: String?,
        channelRoute: AudioChannelRoute,
        playbackDelayMilliseconds: Int,
        stereoSplit: Boolean,
        useShizukuCapture: Boolean,
    ) {
        preferences.edit {
            putString(KEY_PROTOCOL, protocol.name)
            putInt(KEY_CAPTURE_UID, captureUid ?: NO_UID)
            putString(KEY_CAPTURE_PACKAGE, capturePackageName)
            putString(KEY_CHANNEL_ROUTE, channelRoute.name)
            putInt(KEY_PLAYBACK_DELAY_MS, playbackDelayMilliseconds.coerceIn(0, 2_000))
            putBoolean(KEY_STEREO_SPLIT, stereoSplit)
            putBoolean(KEY_SHIZUKU_CAPTURE, useShizukuCapture)
        }
    }

    private companion object {
        const val NO_UID = -1
        const val RENDERER_SEPARATOR = "\u001f"
        const val KEY_PROTOCOL = "protocol"
        const val KEY_CAPTURE_UID = "capture_uid"
        const val KEY_CAPTURE_PACKAGE = "capture_package"
        const val KEY_CHANNEL_ROUTE = "channel_route"
        const val KEY_PLAYBACK_DELAY_MS = "playback_delay_ms"
        const val KEY_RENDERER_IDS = "renderer_ids"
        const val KEY_STEREO_SPLIT = "stereo_split"
        const val KEY_SHIZUKU_CAPTURE = "shizuku_capture"
        const val KEY_MUTE_LOCAL_SPEAKER = "mute_local_speaker"
    }
}
