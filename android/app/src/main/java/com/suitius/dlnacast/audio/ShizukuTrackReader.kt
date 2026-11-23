package com.suitius.dlnacast.audio

import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.media.AudioAttributes
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSession
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Bundle
import android.os.Process
import android.os.SystemClock
import android.os.UserHandle
import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import java.util.Base64
import kotlin.math.roundToInt

private const val MEDIA_CONTENT_CONTROL = "android.permission.MEDIA_CONTENT_CONTROL"
private const val MAX_TEXT_LENGTH = 1_024
private const val MAX_ARTWORK_DIMENSION = 360
private const val MAX_ARTWORK_BYTES = 96 * 1_024

private const val TITLE = "title"
private const val ARTIST = "artist"
private const val ALBUM = "album"
private const val DURATION = "durationMillis"
private const val POSITION = "positionMillis"
private const val ARTWORK = "artworkJpeg"
private const val TRACK_ID = "trackId"

internal data class CapturedTrackMetadata(
    val title: String,
    val artist: String,
    val album: String,
    val durationMillis: Long?,
    val positionMillis: Long?,
    val artworkJpeg: ByteArray?,
    val trackId: String?,
) {
    companion object {
        fun fromBundle(bundle: Bundle?): CapturedTrackMetadata? {
            if (bundle == null) return null
            val title = bundle.getString(TITLE).trackText() ?: return null
            return CapturedTrackMetadata(
                title = title,
                artist = bundle.getString(ARTIST).trackText().orEmpty(),
                album = bundle.getString(ALBUM).trackText().orEmpty(),
                durationMillis = bundle.getLong(DURATION, -1).takeIf { it > 0 },
                positionMillis = bundle.getLong(POSITION, -1).takeIf { it >= 0 },
                artworkJpeg = bundle.getByteArray(ARTWORK)?.takeIf { it.size in 1..MAX_ARTWORK_BYTES },
                trackId = bundle.getString(TRACK_ID).trackText(),
            )
        }
    }
}

/** Reads media sessions in the authorized Shizuku process, under the shell's Binder identity. */
internal class ShizukuTrackReader(private val context: Context) {
    private val appUser = UserHandle.getUserHandleForUid(context.applicationInfo.uid)
    private var artworkCache: CachedArtwork? = null
    private var lastCaptureUid: Int? = null
    private var lastSession: MediaSession.Token? = null

    @Synchronized
    fun read(matchingUid: Int): Bundle? {
        if (lastCaptureUid != matchingUid) {
            lastCaptureUid = matchingUid
            lastSession = null
            artworkCache = null
        }
        val snapshot = try {
            readTrack(matchingUid)
        } catch (_: RuntimeException) {
            null
        }
        if (snapshot == null) artworkCache = null
        return snapshot
    }

    private fun readTrack(matchingUid: Int): Bundle? {
        // The public media-session query is scoped to the process's user.
        if (Process.myUid() != Process.SHELL_UID || Process.myUserHandle() != appUser ||
            context.checkSelfPermission(MEDIA_CONTENT_CONTROL) != PackageManager.PERMISSION_GRANTED
        ) {
            return null
        }

        val matchingPackage = if (matchingUid == ALL_MEDIA_UID) {
            null
        } else {
            if (!Process.isApplicationUid(matchingUid) || UserHandle.getUserHandleForUid(matchingUid) != appUser) {
                return null
            }
            context.packageManager.getPackagesForUid(matchingUid)?.singleOrNull() ?: return null
        }

        val sessions = context.getSystemService(MediaSessionManager::class.java) ?: return null
        val eligibleSessions = sessions.getActiveSessions(null).mapNotNull { controller ->
            if (matchingPackage != null && controller.packageName != matchingPackage) return@mapNotNull null
            val state = controller.playbackState ?: return@mapNotNull null
            if (state.state != PlaybackState.STATE_PLAYING && state.state != PlaybackState.STATE_PAUSED &&
                state.state != PlaybackState.STATE_BUFFERING) return@mapNotNull null
            val info = controller.playbackInfo ?: return@mapNotNull null
            if (info.playbackType != MediaController.PlaybackInfo.PLAYBACK_TYPE_LOCAL) return@mapNotNull null
            when (info.audioAttributes.usage) {
                AudioAttributes.USAGE_MEDIA, AudioAttributes.USAGE_GAME, AudioAttributes.USAGE_UNKNOWN -> controller to state
                else -> null
            }
        }
        val playingSessions = eligibleSessions.filter { (_, state) -> state.state == PlaybackState.STATE_PLAYING }
        if (playingSessions.size > 1) {
            lastSession = null
            return null
        }
        val selected = playingSessions.singleOrNull()
            ?: eligibleSessions.firstOrNull { (controller, _) -> controller.sessionToken == lastSession }
            ?: eligibleSessions.singleOrNull().takeIf { matchingPackage != null }
        val (controller, state) = selected ?: return null
        lastSession = controller.sessionToken
        val metadata = controller.metadata ?: return null
        val title = metadata.getString(MediaMetadata.METADATA_KEY_TITLE).trackText()
            ?: metadata.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE).trackText()
            ?: return null
        val artist = metadata.getString(MediaMetadata.METADATA_KEY_ARTIST).trackText()
            ?: metadata.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST).trackText()
            ?: metadata.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE).trackText()
        val album = metadata.getString(MediaMetadata.METADATA_KEY_ALBUM).trackText()
        val duration = metadata.getLong(MediaMetadata.METADATA_KEY_DURATION).takeIf { it > 0 }
        val mediaId = metadata.getString(MediaMetadata.METADATA_KEY_MEDIA_ID).trackText()
        // An app's media identifier can contain a private file path or URI. Send an opaque identifier.
        val trackId = opaqueTrackId(controller.packageName, mediaId, title, artist, album, duration?.toString())
        val artwork = readArtwork(controller.sessionToken, trackId, metadata)

        return Bundle().apply {
            putString(TITLE, title)
            putString(ARTIST, artist)
            putString(ALBUM, album)
            duration?.let { putLong(DURATION, it) }
            state.positionAtRead(duration)?.let { putLong(POSITION, it) }
            artwork?.let { putByteArray(ARTWORK, it) }
            putString(TRACK_ID, trackId)
        }
    }

    private fun readArtwork(token: MediaSession.Token, trackId: String, metadata: MediaMetadata): ByteArray? {
        artworkCache?.takeIf { it.token == token && it.trackId == trackId }?.let { return it.jpeg }
        artworkCache = null
        val bitmap = metadata.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
            ?: metadata.getBitmap(MediaMetadata.METADATA_KEY_ART)
            ?: metadata.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
            ?: return null
        val jpeg = try {
            bitmap.toBoundedJpeg()
        } catch (_: RuntimeException) {
            null
        }
        artworkCache = CachedArtwork(token, trackId, jpeg)
        return jpeg
    }

    private data class CachedArtwork(val token: MediaSession.Token, val trackId: String, val jpeg: ByteArray?)
}

private fun String?.trackText(): String? = this?.trim()?.take(MAX_TEXT_LENGTH)?.takeIf { it.isNotEmpty() }

private fun PlaybackState.positionAtRead(duration: Long?): Long? {
    if (position < 0) return null
    val now = SystemClock.elapsedRealtime()
    val elapsed = if (state == PlaybackState.STATE_PLAYING && lastPositionUpdateTime in 1L..now) now - lastPositionUpdateTime else 0L
    val estimate = if (playbackSpeed.isFinite()) (position + elapsed * playbackSpeed.toDouble()).toLong() else position
    return estimate.coerceIn(0, duration ?: Long.MAX_VALUE)
}

private fun opaqueTrackId(vararg fields: String?): String {
    val hash = MessageDigest.getInstance("SHA-256")
    for (field in fields) {
        hash.update(field.orEmpty().encodeToByteArray())
        hash.update(0.toByte())
    }
    return Base64.getUrlEncoder().withoutPadding().encodeToString(hash.digest())
}

private fun Bitmap.toBoundedJpeg(): ByteArray? {
    val scale = MAX_ARTWORK_DIMENSION.toDouble() / maxOf(width, height)
    val scaled = if (scale < 1.0) {
        Bitmap.createScaledBitmap(
            this,
            (width * scale).roundToInt().coerceAtLeast(1),
            (height * scale).roundToInt().coerceAtLeast(1),
            true,
        )
    } else {
        this
    }
    return try {
        val output = ByteArrayOutputStream()
        for (quality in listOf(84, 65, 45)) {
            output.reset()
            if (scaled.compress(Bitmap.CompressFormat.JPEG, quality, output) && output.size() in 1..MAX_ARTWORK_BYTES) {
                return output.toByteArray()
            }
        }
        null
    } finally {
        if (scaled !== this) scaled.recycle()
    }
}
