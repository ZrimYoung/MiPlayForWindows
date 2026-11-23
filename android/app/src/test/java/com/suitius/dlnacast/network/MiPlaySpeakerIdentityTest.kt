package com.suitius.dlnacast.network

import com.suitius.dlnacast.miplay.MiPlayCommandCodec
import com.suitius.dlnacast.miplay.MiPlayCommands
import com.suitius.dlnacast.model.SpeakerIdentity
import java.io.ByteArrayOutputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.util.Base64
import java.util.concurrent.FutureTask
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class MiPlaySpeakerIdentityTest {
    @Test
    fun readsNameAndStereoIdentityWithoutChangingPlayback() {
        val loopback = InetAddress.getLoopbackAddress()
        ServerSocket(0, 1, loopback).use { server ->
            server.soTimeout = 3_500
            val checkServer = FutureTask {
                server.accept().use { client ->
                    client.soTimeout = 3_500
                    val input = client.getInputStream().buffered()
                    val output = client.getOutputStream()
                    output.write(MiPlayCommandCodec.encode(MiPlayCommands.LEGACY_CHALLENGE, 17, "1234567890123".encodeToByteArray()))
                    val requests = List(3) { MiPlayCommandCodec.read(input) }
                    assertEquals(listOf(MiPlayCommands.NATIVE_VERSION, MiPlayCommands.LEGACY_CHALLENGE_ACK, MiPlayCommands.GET_DEVICE_INFO), requests.map { it.command })
                    assertEquals(17, requests[1].sequence)
                    assertEquals(1, requests[2].sequence)
                    output.write(MiPlayCommandCodec.encode(MiPlayCommands.NATIVE_VERSION_ACK, 0))
                    output.write(MiPlayCommandCodec.encode(MiPlayCommands.GET_DEVICE_INFO_ACK, 1, identityPayload(
                        "miName" to "小爱音箱 L", "model" to "L09A", "groupId" to "pair-id", "groupName" to "客厅立体声",
                        "channel" to "left", "isMaster" to "1", "accountId" to "ignored",
                    )))
                    assertEquals(-1, input.read())
                }
            }
            thread(isDaemon = true) { checkServer.run() }
            val identity = MiPlaySpeakerIdentityReader().read(loopback.hostAddress.orEmpty(), server.localPort)
            assertEquals(SpeakerIdentity("小爱音箱 L", "L09A", "pair-id", "客厅立体声", "left", true), identity)
            checkServer.get(4, TimeUnit.SECONDS)
        }
    }

    @Test
    fun checksDeclaredLengthAndDistinguishesMissingAndDissolvedGroups() {
        val old = SpeakerIdentity("左边", "L09A", "paired", "客厅", "left", true)
        val name = identityPayload("miName" to "新名字")
        assertFalse(parseSpeakerIdentity(name)!!.hasGroupInfo)
        assertNull(parseSpeakerIdentity(name + byteArrayOf(0)))
        assertNull(parseSpeakerIdentity(name.copyOf(name.size - 1)))
        assertEquals(old.copy(name = "新名字"), mergeSpeakerIdentity(old, parseSpeakerIdentity(name)!!))
        val center = parseSpeakerIdentity(identityPayload("channel" to "center"))!!
        assertTrue(center.hasGroupInfo)
        assertEquals(SpeakerIdentity("左边", "L09A", channel = "center"), mergeSpeakerIdentity(old, center))
    }

    @Test
    fun rejectsGenericAdvertisementsAndKeepsConfirmedNamesAndStableIds() {
        val host = InetAddress.getByName("192.0.2.21")
        val advertisement = MiPlayMdnsDevice("0f005f8d._mi-connect._udp.local", "0F005F8D", host)
        assertNull(advertisement.toReceiver())
        assertNull(advertisement.copy(friendlyName = "小爱音箱", txt = mapOf("apps" to "[1,11]")).toReceiver())
        val receiver = advertisement.copy(txt = mapOf("apps" to "[5]", "idhash" to "AQID")).toReceiver()!!
        assertEquals("miplay://010203", receiver.id)
        assertNull(displayReceiver(receiver, null))
        val name = SpeakerIdentity(name = "书房音箱", groupId = "group", channel = "right")
        val confirmed = displayReceiver(receiver, name)!!
        assertEquals(name.name, confirmed.name)
        assertEquals(receiver.id, confirmed.id)
        assertEquals(name, confirmed.miPlayIdentity)
        val legacy = advertisement.copy(friendlyName = "小爱音箱-6333").toReceiver()!!
        assertEquals(legacy, displayReceiver(legacy, null))
    }

    @Test
    fun advertisedReceiverNameSurvivesWhenItUsesSecureControl() {
        val advertisement = MiPlayMdnsDevice(
            "a0b1c2d3._mi-connect._udp.local", "a0b1c2d3", InetAddress.getByName("192.0.2.22"),
            mapOf("apps" to "[5]", "name" to "ASUS", "appsdata" to "gQAEBIMiww==",
                "version" to "196608", "flags" to "CgE=", "sec" to "2"),
        )
        val receiver = displayReceiver(advertisement.toReceiver()!!, null)!!
        assertEquals("ASUS", receiver.name)
        assertEquals(8899, receiver.miPlayControlPort)
        assertTrue(receiver.usesSecureMiPlay)
        assertFalse(advertisement.copy(txt = advertisement.txt - "sec").toReceiver()!!.usesSecureMiPlay)
        val portDescriptor = byteArrayOf(0x81.toByte(), 0, 4, 4, 0x83.toByte(), 0x27, 0x10)
        val alternate = advertisement.copy(txt = advertisement.txt + ("appsdata" to Base64.getEncoder().encodeToString(portDescriptor)))
        assertEquals(10_000, displayReceiver(alternate.toReceiver()!!, null)?.miPlayControlPort)
    }

    @Test
    fun doesNotShowAnIncompleteAppFiveAdvertisement() {
        val advertisement = MiPlayMdnsDevice(
            "deadbeef._mi-connect._udp.local", "deadbeef", InetAddress.getByName("192.0.2.23"),
            mapOf("apps" to "[5]", "name" to "Nearby phone", "appsdata" to "gQAEBIM="),
        )
        assertNull(displayReceiver(advertisement.toReceiver()!!, null))
        val noName = advertisement.copy(txt = (advertisement.txt - "name") + ("appsdata" to "gQAEBIMiww=="))
        assertNull(displayReceiver(noName.toReceiver()!!, null))
    }

    @Test
    fun appDataReadsOnlyAudioSlotAndRequiresCompleteRecords() {
        val data = byteArrayOf(0x03, 0x02, 0x11, 0x12, 0x03, 0x21, 0x22, 0x23)
        val encoded = Base64.getEncoder().encodeToString(data)
        assertArrayEquals(byteArrayOf(0x21, 0x22, 0x23), audioApplicationData(encoded, listOf(1, 5)))
        assertNull(audioApplicationData(Base64.getEncoder().encodeToString(data.copyOf(data.size - 1)), listOf(1, 5)))
    }

    private fun identityPayload(vararg entries: Pair<String, String>): ByteArray {
        val body = ByteArrayOutputStream().apply {
            for ((key, value) in entries) {
                val bytes = value.encodeToByteArray()
                write(key.length)
                write(key.encodeToByteArray())
                write(0x0c)
                write(bytes.size ushr 8)
                write(bytes.size and 0xff)
                write(bytes)
            }
        }.toByteArray()
        return byteArrayOf((body.size ushr 16).toByte(), (body.size ushr 8).toByte(), body.size.toByte()) + body
    }
}
