package com.suitius.dlnacast.miplay

import java.net.InetSocketAddress
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class MiPlaySecureControlTest {
    @Test
    fun controlKeyUsesReceiverEndpointBeforeSenderEndpoint() {
        val receiver = InetSocketAddress("192.0.2.58", 18999)
        val sender = InetSocketAddress("192.0.2.9", 45000)

        assertEquals("fc49729eeb5ba20cb29da589fee61b11", miPlayControlAuthKey(receiver, sender).decodeToString())
    }

    @Test
    fun wrapsSafetyDataAndMatchesTheFusionPlayCipherFixture() {
        val key = "b0175093fe7e51f8".encodeToByteArray()
        val cipher = MiPlaySecureControlCipher(key)
        val decipher = MiPlaySecureControlCipher(key)
        val auth = "{\n\t\"authMsg\": \"a36d6fd57705e13323c23db0e8a5347c\" \n} \n"
        val acknowledgement = "{\n\t\"authMsgAck\": \"a8b39976c4609052116bcda6ace3211beb0cf14f2b19065cac9397d8d9073934\",\n\t\"result\": \"0\" \n} \n"

        val firstPlain = wrapMiPlaySafetyData("cmd", auth)
        val firstEncrypted = hex(
            "000701e00298a7b4d3" +
                "35105d15b265cb733b043fca060ebd32bc490d86f63d3f00" +
                "0e225b893c09e9c12af0f54fb686032a931ea9a2c8d09420" +
                "b3a2a88a2e767964fef89395ca9b65c7",
        )
        assertArrayEquals(firstEncrypted, cipher.encrypt(firstPlain))
        assertArrayEquals(firstPlain, decipher.decrypt(firstEncrypted))

        val secondPlain = wrapMiPlaySafetyData("ack", acknowledgement)
        val secondEncrypted = hex(
            "000701e00fd8221c81" +
                "9d8ff72441d16e76e8dbde0169ce9f8bd9dcdff2d2ab69" +
                "b3a6e78171efbe463aec933bc3ec5334df87c6f634171618" +
                "b5947c071ed477350e8779d595db54d845cf141050cfaacd" +
                "99c2bf5b8bd220c7cbcb29cf877ef1a49beb89478fd7d2f" +
                "d367f038d8cf86228b03b9cd396ca29f0024741916693f89" +
                "b176314f89aedb6690a",
        )
        assertArrayEquals(secondEncrypted, cipher.encrypt(secondPlain))
        assertArrayEquals(secondPlain, decipher.decrypt(secondEncrypted))
    }

    @Test
    fun rejectsPlaintextAndCorruptionWithoutAdvancingTheCipher() {
        val key = "0123456789abcdef".encodeToByteArray()
        val cipher = MiPlaySecureControlCipher(key)
        val decipher = MiPlaySecureControlCipher(key)
        val body = cipher.encrypt(ByteArray(0))
        val corrupted = body.copyOf().apply { this[lastIndex] = (this[lastIndex].toInt() xor 1).toByte() }

        assertThrows(IllegalStateException::class.java) { decipher.decrypt(byteArrayOf(0, 0, 0, 0, 0)) }
        assertThrows(IllegalStateException::class.java) { decipher.decrypt(corrupted) }
        assertArrayEquals(ByteArray(0), decipher.decrypt(body))
    }

    private fun hex(value: String): ByteArray = value.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
}
