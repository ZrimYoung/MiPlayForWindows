package com.suitius.dlnacast.miplay

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec
import org.json.JSONObject

/** FusionPlay's MiPlay control profile, with separate cipher chains for each direction. */
internal class MiPlaySecureControl(socket: Socket) {
    private val authKey = miPlayControlAuthKey(
        socket.remoteSocketAddress as InetSocketAddress,
        socket.localSocketAddress as InetSocketAddress,
    )
    private val incoming = MiPlaySecureControlCipher(authKey.copyOfRange(0, CONTROL_BLOCK_SIZE))
    private val outgoing = MiPlaySecureControlCipher(authKey.copyOfRange(0, CONTROL_BLOCK_SIZE))

    fun authenticate(input: BufferedInputStream, output: BufferedOutputStream) {
        val identification = MiPlayCommandCodec.read(input)
        check(identification.command == MiPlayCommands.LEGACY_CHALLENGE &&
            identification.payload.decodeToString().matches(NUMERIC_DEVICE_ID)) {
            "MiPlay 接收端没有发送有效的设备标识"
        }

        output.write(MiPlayCommandCodec.encode(SAFETY_INFO, 1, wrapMiPlaySafetyData("cmd", SAFETY_OFFER)))
        output.flush()
        val profile = readHandshake(input, SAFETY_INFO_ACK, 1, "ack", encrypted = false)
        check(profile.optString("result") == "0" && profile.optString("aesIvType") == "2" &&
            profile.optString("aesKeyType") == "1" && profile.optString("authAlgorithmType") == "4" &&
            profile.optString("authKeyType") == "1" && profile.optString("integrityType") == "1") {
            "MiPlay 接收端拒绝了安全参数或返回了不支持的安全模式"
        }

        val peerFrame = MiPlayCommandCodec.read(input)
        val peerChallenge = decodeHandshake(peerFrame, SAFETY_AUTH, "cmd", encrypted = true).optString("authMsg")
        check(peerChallenge.matches(HEX_CHALLENGE)) { "MiPlay 接收端返回了无效的鉴权挑战" }
        val localChallenge = ByteArray(16).also(SecureRandom()::nextBytes).toLowerHex()

        output.write(encode(SAFETY_AUTH, 2, wrapMiPlaySafetyData("cmd", """{"authMsg":"$localChallenge"}""")))
        val peerProof = authProof(peerChallenge)
        output.write(encode(SAFETY_AUTH_ACK, peerFrame.sequence, wrapMiPlaySafetyData(
            "ack", """{"authMsgAck":"$peerProof","result":"0"}""",
        )))
        output.flush()

        val acknowledgement = readHandshake(input, SAFETY_AUTH_ACK, 2, "ack", encrypted = true)
        val proof = acknowledgement.optString("authMsgAck").toByteArray(Charsets.US_ASCII)
        check(acknowledgement.optString("result") == "0" &&
            MessageDigest.isEqual(proof, authProof(localChallenge).toByteArray(Charsets.US_ASCII))) {
            "MiPlay 接收端未通过双向鉴权"
        }
    }

    fun encode(command: Int, sequence: Int, payload: ByteArray): ByteArray =
        MiPlayCommandCodec.encode(command, sequence, outgoing.encrypt(payload))

    fun read(input: BufferedInputStream): MiPlayCommandFrame {
        val frame = MiPlayCommandCodec.read(input)
        return frame.copy(payload = incoming.decrypt(frame.payload))
    }

    private fun readHandshake(
        input: BufferedInputStream,
        command: Int,
        sequence: Int,
        key: String,
        encrypted: Boolean,
    ): JSONObject {
        val frame = MiPlayCommandCodec.read(input)
        check(frame.sequence == sequence) { "MiPlay 安全确认序号不匹配" }
        return decodeHandshake(frame, command, key, encrypted)
    }

    private fun decodeHandshake(
        frame: MiPlayCommandFrame,
        command: Int,
        key: String,
        encrypted: Boolean,
    ): JSONObject {
        check(frame.command == command) { "MiPlay 接收端返回了意外的鉴权命令" }
        val body = if (encrypted) incoming.decrypt(frame.payload) else frame.payload
        return JSONObject(unwrapMiPlaySafetyData(key, body))
    }

    private fun authProof(challenge: String): String = Mac.getInstance("HmacSHA256").run {
        init(SecretKeySpec(authKey, algorithm))
        doFinal(challenge.toByteArray(Charsets.US_ASCII)).toLowerHex()
    }

    private companion object {
        const val SAFETY_INFO = 0x1400
        const val SAFETY_INFO_ACK = 0x1401
        const val SAFETY_AUTH = 0x1402
        const val SAFETY_AUTH_ACK = 0x1403
        const val SAFETY_OFFER = """{"aesIvTypes":"3","aesKeyTypes":"3","authAlgorithmTypes":"7","authKeyTypes":"1","integrityTypes":"1"}"""
        val NUMERIC_DEVICE_ID = Regex("[0-9]{12,17}")
        val HEX_CHALLENGE = Regex("[a-f0-9]{32}")
    }
}

private const val CONTROL_BLOCK_SIZE = 16
private const val SAFETY_HEADER_SIZE = 9
private val safetyMagic = byteArrayOf(0, 7, 1, 0xe0.toByte())

internal class MiPlaySecureControlCipher(key: ByteArray) {
    private val aesKey = SecretKeySpec(key, "AES")
    private var chainingIv = key.copyOf()

    fun encrypt(plaintext: ByteArray): ByteArray {
        val padding = CONTROL_BLOCK_SIZE - plaintext.size % CONTROL_BLOCK_SIZE
        val ciphertext = crypt(Cipher.ENCRYPT_MODE, plaintext.copyOf(plaintext.size + padding))
        chainingIv = ciphertext.takeLastBlock()
        return ByteBuffer.allocate(SAFETY_HEADER_SIZE + ciphertext.size)
            .put(safetyMagic)
            .put(padding.toByte())
            .putInt(miPlayIntegrity(ciphertext))
            .put(ciphertext)
            .array()
    }

    fun decrypt(body: ByteArray): ByteArray {
        check(body.size >= SAFETY_HEADER_SIZE + CONTROL_BLOCK_SIZE &&
            (body.size - SAFETY_HEADER_SIZE) % CONTROL_BLOCK_SIZE == 0 &&
            safetyMagic.indices.all { body[it] == safetyMagic[it] }) {
            "MiPlay 接收端没有返回有效的加密控制消息"
        }
        val padding = body[safetyMagic.size].toInt() and 0xff
        check(padding in 1..CONTROL_BLOCK_SIZE) { "MiPlay 加密控制消息填充长度无效" }
        val ciphertext = body.copyOfRange(SAFETY_HEADER_SIZE, body.size)
        check(ByteBuffer.wrap(body, safetyMagic.size + 1, Int.SIZE_BYTES).int == miPlayIntegrity(ciphertext)) {
            "MiPlay 加密控制消息校验失败"
        }
        val plaintext = crypt(Cipher.DECRYPT_MODE, ciphertext)
        val size = plaintext.size - padding
        check((size until plaintext.size).all { plaintext[it] == 0.toByte() }) { "MiPlay 加密控制消息填充无效" }
        chainingIv = ciphertext.takeLastBlock()
        return plaintext.copyOf(size)
    }

    private fun crypt(mode: Int, bytes: ByteArray): ByteArray {
        val cipher = Cipher.getInstance("AES/CBC/NoPadding")
        cipher.init(mode, aesKey, IvParameterSpec(chainingIv))
        return cipher.doFinal(bytes)
    }

    private fun ByteArray.takeLastBlock() = copyOfRange(size - CONTROL_BLOCK_SIZE, size)
}

internal fun miPlayControlAuthKey(receiver: InetSocketAddress, sender: InetSocketAddress): ByteArray {
    val endpoints = "${receiver.address.hostAddress}${receiver.port}${sender.address.hostAddress}${sender.port}"
    val material = endpoints.map { if (it in '0'..'9') it + 0x31 else it }.joinToString("")
    return MessageDigest.getInstance("MD5").digest(material.toByteArray(Charsets.US_ASCII))
        .toLowerHex().toByteArray(Charsets.US_ASCII)
}

// MiPlay integrity type 1 uses a byte-swapped CRC-32 table, with no final XOR.
private val integrityTable = IntArray(256) { index ->
    var value = index shl 24
    repeat(8) {
        value = if (value < 0) (value shl 1) xor 0x04c11db7 else value shl 1
    }
    Integer.reverseBytes(value)
}

private fun miPlayIntegrity(ciphertext: ByteArray): Int {
    var value = -1
    for (byte in ciphertext) {
        value = integrityTable[(value xor byte.toInt()) and 0xff] xor (value ushr 8)
    }
    return value
}

internal fun wrapMiPlaySafetyData(key: String, json: String): ByteArray {
    val label = key.toByteArray(Charsets.US_ASCII)
    val data = json.toByteArray(Charsets.UTF_8)
    require(label.size in 1..255 && data.size <= 255)
    return ByteBuffer.allocate(label.size + data.size + 6)
        .put(label.size.toByte())
        .put(label)
        .put(byteArrayOf(30, 0, 0, 0))
        .put(data.size.toByte())
        .put(data)
        .array()
}

private fun unwrapMiPlaySafetyData(key: String, bytes: ByteArray): String {
    val label = key.toByteArray(Charsets.US_ASCII)
    val headerSize = label.size + 6
    check(bytes.size >= headerSize && (bytes[0].toInt() and 0xff) == label.size &&
        label.indices.all { bytes[it + 1] == label[it] } &&
        ByteBuffer.wrap(bytes, label.size + 1, Int.SIZE_BYTES).int == 0x1e000000 &&
        (bytes[headerSize - 1].toInt() and 0xff) == bytes.size - headerSize) {
        "MiPlay 安全消息内容格式无效"
    }
    return bytes.decodeToString(startIndex = headerSize)
}

private fun ByteArray.toLowerHex() = joinToString("") { "%02x".format(it) }
