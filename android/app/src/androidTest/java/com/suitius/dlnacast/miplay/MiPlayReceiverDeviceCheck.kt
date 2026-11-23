package com.suitius.dlnacast.miplay

import com.suitius.dlnacast.audio.PcmAudioCapture
import com.suitius.dlnacast.audio.PcmFrameBuffer
import com.suitius.dlnacast.audio.RoutedPcmOutput
import com.suitius.dlnacast.model.DlnaRenderer
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlin.concurrent.thread

/** Exercises the production session with silent generated PCM and an explicitly selected test receiver. */
object MiPlayReceiverDeviceCheck {
    @JvmStatic
    fun main(args: Array<String>) = runBlocking {
        require(args.size == 3) { "Expected receiver IPv4, control port and secure or legacy mode" }
        val (host, port, mode) = args
        require(mode == "secure" || mode == "legacy")
        val renderer = DlnaRenderer(
            id = "miplay://device-check",
            name = "MiPlay connection test",
            descriptionUrl = "miplay://$host",
            host = host,
            avTransportControlUrl = "",
            renderingControlUrl = null,
            connectionManagerControlUrl = null,
            supportsMiPlay = true,
            miPlayControlPort = port.toInt(),
            usesSecureMiPlay = mode == "secure",
        )
        MiPlayCastSession(listOf(renderer), ::SilentPcm, report = ::println).use { session ->
            session.start()
            delay(2_500)
            check(session.failureMessage == null) { session.failureMessage.orEmpty() }
            println(session.streamDetail)
            println("PASS: $mode receiver setup and AAC delivery from Android")
        }
    }

    private class SilentPcm(private val outputs: List<RoutedPcmOutput>) : PcmAudioCapture {
        override val supportsUidFilter = false
        private var producer: Thread? = null

        override fun start() {
            producer = thread(isDaemon = true, name = "MiPlay test PCM") {
                val frame = ByteArray(PcmFrameBuffer.MIPLAY_FRAME_BYTES)
                try {
                    while (!Thread.currentThread().isInterrupted) {
                        outputs.forEach { it.buffer.offer(frame) }
                        Thread.sleep(21)
                    }
                } catch (_: InterruptedException) {
                    Thread.currentThread().interrupt()
                }
            }
        }

        override suspend fun setMatchingUid(matchingUid: Int?) = Unit

        override fun close() {
            producer?.interrupt()
            producer?.join(500)
        }
    }
}
