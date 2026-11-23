package com.suitius.dlnacast.miplay

import java.io.BufferedInputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class MiPlayRtspTest {
    @Test
    fun `FusionPlay negotiates version two and acknowledges latency feedback`() = verifyHandshake(fusionPlay = true)

    @Test
    fun `legacy speakers still receive the legacy WFD negotiation`() = verifyHandshake(fusionPlay = false)

    private fun verifyHandshake(fusionPlay: Boolean) = runBlocking<Unit> {
        val address = InetAddress.getByName("127.0.0.1")
        ServerSocket(0, 1, address).use { listener ->
            Socket(address, listener.localPort).use { receiver ->
                receiver.soTimeout = 3_000
                val ready = CompletableDeferred<Long>()
                val server = MiPlayRtspServer(
                    socket = listener.accept(),
                    sourceAddress = address,
                    timerPort = 43_210,
                    playbackDelayMicroseconds = 100_000,
                    sharedTimeOffsetProvider = { 123_456L },
                    fusionPlay = fusionPlay,
                    report = {},
                )
                val serverTask = async(Dispatchers.IO) {
                    runCatching { server.run(onPlay = {}, onReady = { ready.complete(it) }) }
                }
                try {
                    val input = BufferedInputStream(receiver.getInputStream())
                    val options = readRtspMessage(input)
                    assertEquals("OPTIONS * RTSP/1.0", options.startLine)
                    assertEquals("2130706433:43210", options.header("wfd_timer_server_port"))
                    receiver.reply(1)
                    receiver.writeRtsp("OPTIONS * RTSP/1.0", 1, "Require: org.wfa.wfd1.0")
                    assertEquals(1, readRtspMessage(input).cseq)

                    val capabilityQuery = readRtspMessage(input)
                    assertTrue(capabilityQuery.startLine.startsWith("GET_PARAMETER "))
                    assertEquals(2, capabilityQuery.cseq)
                    val queriedCodecs = capabilityQuery.body.toString(Charsets.US_ASCII)
                        .lineSequence().filter { it.startsWith("wfd_audio_codecs") }.toList()
                    assertEquals(listOf(if (fusionPlay) "wfd_audio_codecs_v2" else "wfd_audio_codecs"), queriedCodecs)
                    val capability = if (fusionPlay) "wfd_audio_codecs_v2: 15 3 3" else "wfd_audio_codecs: AAC 00000001 00"
                    receiver.reply(2, "$capability\r\n")

                    assertEquals(3, readRtspMessage(input).cseq)
                    receiver.reply(3)
                    val trigger = readRtspMessage(input)
                    assertEquals(4, trigger.cseq)
                    assertEquals("wfd_trigger_method: SETUP\r\n", trigger.body.toString(Charsets.US_ASCII))
                    receiver.reply(4)

                    val presentation = "rtsp://127.0.0.1/wfd1.0/streamid=0"
                    receiver.writeRtsp("SETUP $presentation RTSP/1.0", 2, "Transport: RTP/AVP/TCP;interleaved=0-1")
                    val setup = readRtspMessage(input)
                    assertEquals("RTSP/1.0 200 OK", setup.startLine)
                    assertEquals(2, setup.cseq)
                    receiver.writeRtsp("PLAY $presentation RTSP/1.0", 3, "Session: ${setup.header("Session")}")
                    assertEquals(3, readRtspMessage(input).cseq)

                    val offset = readRtspMessage(input)
                    assertTrue(offset.startLine.startsWith("TIME_OFFSET "))
                    assertEquals("123456", offset.header("TimeOffset"))
                    receiver.reply(5)
                    assertEquals(123_456L, withTimeout(3_000) { ready.await() })

                    if (fusionPlay) {
                        receiver.writeRtsp("VIDEO_LATENCY $presentation RTSP/1.0", 1_000)
                        val telemetryResponse = readRtspMessage(input)
                        assertEquals("RTSP/1.0 200 OK", telemetryResponse.startLine)
                        assertEquals(1_000, telemetryResponse.cseq)
                    }
                    Unit
                } finally {
                    server.close()
                    serverTask.await()
                }
            }
        }
    }

    private fun Socket.reply(sequence: Int, body: String = "") =
        writeRtsp("RTSP/1.0 200 OK", sequence, body = body)

    private fun Socket.writeRtsp(startLine: String, sequence: Int, header: String = "", body: String = "") {
        val wire = buildString {
            append("$startLine\r\nCSeq: $sequence\r\n")
            if (header.isNotEmpty()) append("$header\r\n")
            append("Content-Length: ${body.length}\r\n\r\n$body")
        }
        getOutputStream().write(wire.toByteArray(Charsets.US_ASCII))
    }
}
