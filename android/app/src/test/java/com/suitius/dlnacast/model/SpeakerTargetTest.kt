package com.suitius.dlnacast.model

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SpeakerTargetTest {
    @Test
    fun confirmedPairUsesTheReceiverGroupIdAndLeftRightOrder() {
        val left = receiver("left", "客厅左", "pair-a", "left")
        val right = receiver("right", "客厅右", "pair-a", "right")
        val anotherRight = receiver("other", "客厅右", "pair-b", "right")

        val targets = speakerTargets(listOf(right, anotherRight, left), useNativeGroups = true)
        val complete = targets.single { it.isAvailable }
        assertEquals("group:pair-a", complete.id)
        assertEquals("客厅立体声", complete.name)
        assertEquals(listOf(left, right), complete.renderers)
        assertTrue(complete.isStereoPair)
        assertFalse(targets.single { it.id == "group:pair-b" }.isAvailable)
    }

    @Test
    fun duplicateChannelsDoNotFormACompletePair() {
        val targets = speakerTargets(listOf(
            receiver("one", "一", "same", "left"),
            receiver("two", "二", "same", "left"),
        ), useNativeGroups = true)
        assertFalse(targets.single().isAvailable)
    }

    @Test
    fun manualSplitStillLetsUsersSelectPhysicalReceivers() {
        val targets = speakerTargets(listOf(
            receiver("left", "客厅左", "pair-a", "left"),
            receiver("right", "客厅右", "pair-a", "right"),
        ), useNativeGroups = false)
        assertEquals(listOf("speaker:left", "speaker:right"), targets.map(SpeakerTarget::id))
        assertTrue(targets.all { it.isAvailable && !it.isStereoPair })
    }

    private fun receiver(id: String, name: String, groupId: String, channel: String) = DlnaRenderer(
        id, name, "miplay://$id", id, "", null, null, supportsMiPlay = true,
        miPlayIdentity = SpeakerIdentity(name = name, groupId = groupId, groupName = "客厅立体声", channel = channel),
    )
}
