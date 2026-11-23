package com.suitius.dlnacast.audio

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ShizukuSpeakerMuteTest {
    @Test
    fun repeatedMuteRestoresOriginalSpeakerIndexOnClose() {
        val volume = FakeSpeakerVolume(9)
        val mute = ShizukuSpeakerMute(volume)

        assertTrue(mute.setMuted(true))
        assertTrue(mute.setMuted(true))
        assertEquals(0, volume.speakerIndex)
        mute.close()

        assertEquals(listOf(0, 9), volume.writes)
        assertFalse(mute.isMuted())
    }

    @Test
    fun endingCastLeavesAManualVolumeChangeInPlace() {
        val volume = FakeSpeakerVolume(9)
        val mute = ShizukuSpeakerMute(volume)
        mute.setMuted(true)
        volume.speakerIndex = 5

        assertFalse(mute.setMuted(false))

        assertEquals(5, volume.speakerIndex)
        assertEquals(listOf(0), volume.writes)
    }

    @Test
    fun endingCastLeavesAManualMuteInPlace() {
        val volume = FakeSpeakerVolume(9)
        val mute = ShizukuSpeakerMute(volume)
        mute.setMuted(true)
        volume.streamMuted = true

        mute.close()

        assertEquals(0, volume.speakerIndex)
        assertEquals(listOf(0), volume.writes)
    }

    private class FakeSpeakerVolume(var speakerIndex: Int) : ShizukuSpeakerMute.SpeakerVolume {
        var streamMuted = false
        val writes = mutableListOf<Int>()

        override fun index(): Int = speakerIndex
        override fun isStreamMuted(): Boolean = streamMuted
        override fun setIndex(index: Int) {
            speakerIndex = index
            writes += index
        }
    }
}
