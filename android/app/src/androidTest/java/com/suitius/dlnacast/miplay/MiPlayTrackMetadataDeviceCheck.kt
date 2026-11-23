package com.suitius.dlnacast.miplay

import java.util.Base64
import org.json.JSONObject

/** Validates the wire JSON against Android's actual platform JSON implementation. */
object MiPlayTrackMetadataDeviceCheck {
    @JvmStatic
    fun main(args: Array<String>) {
        val fixture = MiPlayTrackMetadata(
            title = "曲目 🎧 \"Live\"",
            artist = "A\\B",
            album = "现场\n演出",
            durationMillis = 240_000,
            positionMillis = 42_000,
            artworkJpeg = byteArrayOf(0xff.toByte(), 0xd8.toByte(), 0xff.toByte(), 0xd9.toByte()),
            trackId = "player:track/42",
        )
        val actual = JSONObject(encodeMiPlayTrackMetadata(fixture, "MiPlay Cast", 37, 0).decodeToString())
        check(actual.getString("mTitle") == fixture.title)
        check(actual.getString("mArtist") == fixture.artist)
        check(actual.getString("mAlbum") == fixture.album)
        check(actual.getLong("mDuration") == fixture.durationMillis)
        check(actual.getLong("mPosition") == fixture.positionMillis)
        check(actual.getString("id") == fixture.trackId)
        check(Base64.getDecoder().decode(actual.getString("mArt")).contentEquals(fixture.artworkJpeg))
        check(actual.getInt("volume") == 37)

        val unavailable = fixture.copy(durationMillis = null, positionMillis = -1,
            artworkJpeg = ByteArray(MAX_MIPLAY_ARTWORK_BYTES + 1))
        val limited = JSONObject(encodeMiPlayTrackMetadata(unavailable, "MiPlay Cast", 37, 0).decodeToString())
        check(limited.getInt("mDuration") == 0)
        check(!limited.has("mPosition"))
        check(limited.getString("mArt").isEmpty())

        val fallback = JSONObject(encodeMiPlayTrackMetadata(null, "MI PAD 4/Plus", 24, Int.MAX_VALUE).decodeToString())
        check(fallback.getString("mTitle") == "System Audio")
        check(fallback.getInt("mDuration") == Int.MAX_VALUE)
        println("PASS: MiPlay track metadata, artwork limits and missing-source fallback")
    }
}
