package com.suitius.dlnacast.network

import com.suitius.dlnacast.miplay.MiPlayCommandCodec
import com.suitius.dlnacast.miplay.MiPlayCommandFrame
import com.suitius.dlnacast.miplay.MiPlayCommands
import com.suitius.dlnacast.miplay.legacyChallengeResponse
import com.suitius.dlnacast.model.SpeakerIdentity
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import java.util.Locale
import java.util.concurrent.TimeUnit

/** Reads the receiver's own name and group without opening a playback session. */
internal class MiPlaySpeakerIdentityReader {
    fun read(host: String, port: Int = 8899): SpeakerIdentity? = Socket().use { socket ->
        socket.tcpNoDelay = true
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(QUERY_TIMEOUT_MS)
        socket.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
        val input = socket.getInputStream().buffered()
        val challenge = readFrame(socket, input, deadline)
        if (challenge.command != MiPlayCommands.LEGACY_CHALLENGE ||
            challenge.payload.size !in 12..17 ||
            challenge.payload.any { it.toInt() !in 48..57 }
        ) return null

        val output = socket.getOutputStream().buffered()
        output.write(MiPlayCommandCodec.encode(MiPlayCommands.NATIVE_VERSION, 0, SOURCE_VERSION))
        output.write(MiPlayCommandCodec.encode(
            MiPlayCommands.LEGACY_CHALLENGE_ACK, challenge.sequence, legacyChallengeResponse(challenge.payload),
        ))
        output.write(MiPlayCommandCodec.encode(MiPlayCommands.GET_DEVICE_INFO, QUERY_SEQUENCE))
        output.flush()

        repeat(8) {
            val response = readFrame(socket, input, deadline)
            if (response.command == MiPlayCommands.GET_DEVICE_INFO_ACK && response.sequence == QUERY_SEQUENCE) {
                return parseSpeakerIdentity(response.payload)
            }
        }
        null
    }

    private fun readFrame(socket: Socket, input: InputStream, deadline: Long): MiPlayCommandFrame {
        val header = ByteBuffer.wrap(readExactly(socket, input, 9, deadline))
        if (header.get() != '$'.code.toByte()) throw IOException("Invalid MiPlay identity response")
        val command = header.short.toInt() and 0xffff
        val sequence = header.short.toInt() and 0xffff
        val size = header.int
        if (size !in 0..64 * 1024) throw IOException("Invalid MiPlay identity payload length")
        return MiPlayCommandFrame(command, sequence, readExactly(socket, input, size, deadline))
    }

    private fun readExactly(socket: Socket, input: InputStream, size: Int, deadline: Long): ByteArray {
        val result = ByteArray(size)
        var offset = 0
        while (offset < size) {
            val nanosLeft = deadline - System.nanoTime()
            if (nanosLeft <= 0) throw SocketTimeoutException("MiPlay identity read timed out")
            socket.soTimeout = TimeUnit.NANOSECONDS.toMillis(nanosLeft).coerceAtLeast(1).toInt()
            val read = input.read(result, offset, size - offset)
            if (read == -1) throw EOFException("MiPlay identity connection closed")
            offset += read
        }
        return result
    }

    private companion object {
        const val QUERY_TIMEOUT_MS = 3_000L
        const val CONNECT_TIMEOUT_MS = 900
        const val QUERY_SEQUENCE = 1
        val SOURCE_VERSION = "1.0.1123012\u0000".toByteArray(Charsets.US_ASCII)
    }
}

/** Cmd_GetDeviceInfo (legacy) has a 24-bit body length and repeated UTF-8 string fields. */
internal fun parseSpeakerIdentity(payload: ByteArray): SpeakerIdentity? {
    fun unsigned(index: Int): Int = payload[index].toInt() and 0xff
    if (payload.size < 3) return null
    val bodySize = (unsigned(0) shl 16) or (unsigned(1) shl 8) or unsigned(2)
    if (bodySize != payload.size - 3 || bodySize == 0) return null
    val fields = mutableMapOf<String, String>()
    var offset = 3
    while (offset < payload.size) {
        val keySize = unsigned(offset++)
        if (keySize == 0 || payload.size - offset < keySize + 3) return null
        val key = payload.decodeToString(offset, offset + keySize)
        offset += keySize
        if (unsigned(offset++) != 0x0c) return null
        val valueSize = (unsigned(offset) shl 8) or unsigned(offset + 1)
        offset += 2
        if (valueSize > payload.size - offset) return null
        if (key in SPEAKER_FIELDS && key !in fields) {
            fields[key] = payload.decodeToString(offset, offset + valueSize).trim()
                .takeIf { it.length <= 256 && it.none(Char::isISOControl) }.orEmpty()
        }
        offset += valueSize
    }
    if (fields.isEmpty()) return null
    val channel = fields["channel"]?.lowercase(Locale.ROOT).orEmpty()
    return SpeakerIdentity(
        name = fields["miName"].orEmpty(),
        model = fields["model"].orEmpty(),
        groupId = fields["groupId"].orEmpty(),
        groupName = fields["groupName"].orEmpty(),
        channel = channel,
        isMaster = fields["isMaster"] == "1",
        hasGroupInfo = "groupId" in fields || channel == "center",
    )
}

private val SPEAKER_FIELDS = setOf("miName", "model", "groupId", "groupName", "channel", "isMaster")
