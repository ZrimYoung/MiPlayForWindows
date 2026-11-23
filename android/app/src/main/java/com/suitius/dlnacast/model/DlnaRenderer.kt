package com.suitius.dlnacast.model

data class DlnaRenderer(
    val id: String,
    val name: String,
    val descriptionUrl: String,
    val host: String,
    val avTransportControlUrl: String,
    val renderingControlUrl: String?,
    val connectionManagerControlUrl: String?,
    val supportsMiPlay: Boolean = false,
    val miPlayIdentity: SpeakerIdentity? = null,
    val miPlayControlPort: Int? = null,
    val usesSecureMiPlay: Boolean = false,
)

data class SpeakerIdentity(
    val name: String = "",
    val model: String = "",
    val groupId: String = "",
    val groupName: String = "",
    val channel: String = "",
    val isMaster: Boolean = false,
    val hasGroupInfo: Boolean = groupId.isNotEmpty() || channel == "center",
)
