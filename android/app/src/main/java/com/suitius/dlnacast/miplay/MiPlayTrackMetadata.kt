package com.suitius.dlnacast.miplay

import java.util.Base64
import org.json.JSONObject

internal data class MiPlayTrackMetadata(
    val title: String,
    val artist: String = "",
    val album: String = "",
    val durationMillis: Long? = null,
    val positionMillis: Long? = null,
    val artworkJpeg: ByteArray? = null,
    val trackId: String? = null,
)

internal const val MAX_MIPLAY_ARTWORK_BYTES = 128 * 1024
private const val MAX_TEXT_LENGTH = 1024

internal fun encodeMiPlayTrackMetadata(
    metadata: MiPlayTrackMetadata?,
    sourceName: String,
    volume: Int,
    unknownDurationMillis: Int,
): ByteArray {
    val duration = metadata?.durationMillis?.takeIf { it > 0 }
        ?.coerceAtMost(Int.MAX_VALUE.toLong())
    val position = metadata?.positionMillis?.takeIf { it >= 0 }
        ?.coerceAtMost(duration ?: Int.MAX_VALUE.toLong())
    val artwork = metadata?.artworkJpeg?.takeIf { it.isNotEmpty() && it.size <= MAX_MIPLAY_ARTWORK_BYTES }
        ?.let { Base64.getEncoder().encodeToString(it) }.orEmpty()
    val mediaInfo = JSONObject()
        .put("mArtist", if (metadata == null) "Android" else metadata.artist.wireText())
        .put("mAlbum", metadata?.album.wireText())
        .put("mTitle", metadata?.title.wireText().ifEmpty { "System Audio" })
        .put("mDuration", duration?.toInt() ?: unknownDurationMillis)
        .put("id", metadata?.trackId.wireText())
        .put("mCoverUrl", "")
        .put("status", 0)
        .put("volume", volume)
        .put("mArt", artwork)
        .put("mSourceName", sourceName)
        .put("mDeviceState", 2)
    if (position != null) mediaInfo.put("mPosition", position)
    return mediaInfo.toString().encodeToByteArray()
}

private fun String?.wireText() = this?.trim()?.take(MAX_TEXT_LENGTH).orEmpty()
