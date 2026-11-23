package com.suitius.dlnacast.model

internal data class SpeakerTarget(
    val id: String,
    val name: String,
    val renderers: List<DlnaRenderer>,
    val isStereoPair: Boolean,
    val isAvailable: Boolean = true,
)

internal fun speakerTargets(renderers: List<DlnaRenderer>, useNativeGroups: Boolean): List<SpeakerTarget> {
    if (!useNativeGroups) return renderers.map(::singleTarget)
    val (members, singles) = renderers.partition {
        val identity = it.miPlayIdentity
        identity != null && identity.groupId.isNotBlank() && identity.channel in setOf("left", "right")
    }
    val groups = members.groupBy { it.miPlayIdentity!!.groupId }.map { (groupId, receivers) ->
        val ordered = receivers.sortedBy { it.miPlayIdentity!!.channel }
        val available = ordered.size == 2 && ordered.map { it.miPlayIdentity!!.channel } == listOf("left", "right")
        val groupName = ordered.firstNotNullOfOrNull { it.miPlayIdentity?.groupName?.takeIf(String::isNotBlank) }
        SpeakerTarget("group:$groupId", groupName ?: "${ordered.first().name} · 立体声", ordered, true, available)
    }
    return (groups + singles.map(::singleTarget)).sortedWith(compareBy({ !it.isStereoPair }, { it.name }))
}

private fun singleTarget(renderer: DlnaRenderer) = SpeakerTarget(
    "speaker:${renderer.id}", renderer.name, listOf(renderer), false,
)
