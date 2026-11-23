package com.suitius.dlnacast.service

import com.suitius.dlnacast.model.DlnaRenderer
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

sealed interface CastStatus {
    data object Idle : CastStatus
    data class Preparing(val message: String) : CastStatus
    data class Streaming(
        val rendererName: String,
        val detail: String,
        val supportsPlaybackControl: Boolean = false,
        val receiverPaused: Boolean = false,
        val receiverVolume: Int? = null,
        val receiverControls: List<ReceiverControlState> = emptyList(),
        val supportsCaptureSwitch: Boolean = false,
        val protocol: CastProtocol = CastProtocol.MIPLAY,
        val renderers: List<DlnaRenderer> = emptyList(),
        val stereoSplit: Boolean = false,
        val localSpeakerMuted: Boolean? = null,
        val localSpeakerMuteError: String? = null,
    ) : CastStatus
    data class Error(val message: String) : CastStatus
}

object CastStateStore {
    private val mutableStatus = MutableStateFlow<CastStatus>(CastStatus.Idle)
    val status: StateFlow<CastStatus> = mutableStatus.asStateFlow()

    fun update(status: CastStatus) {
        mutableStatus.value = status
    }
}
