package com.suitius.dlnacast.network

import com.suitius.dlnacast.model.DlnaRenderer
import com.suitius.dlnacast.model.SpeakerIdentity
import java.net.Inet4Address
import java.util.Base64
import java.util.Locale
import org.json.JSONObject

private const val AUDIO_APP = 5
private val opaqueName = Regex("(?i)(?:0x)?[0-9a-f][0-9a-f:-]{5,}")

internal fun readableSpeakerName(value: String?): String? = value?.trim()?.takeIf { name ->
    name.isNotEmpty() && name.length <= 128 && name.none(Char::isISOControl) && !opaqueName.matches(name)
}

internal fun advertisedSpeakerName(value: String?): Boolean = readableSpeakerName(value)?.let { name ->
    name.contains("音箱") || name.contains("speaker", ignoreCase = true)
} == true

internal fun MiPlayMdnsDevice.toReceiver(): DlnaRenderer? {
    val endpoint = address as? Inet4Address ?: return null
    if (endpoint.isAnyLocalAddress || endpoint.isLoopbackAddress || endpoint.isMulticastAddress) return null
    val apps = txt["apps"]?.trim()?.removeSurrounding("[", "]")?.split(',')?.map { it.trim().toIntOrNull() }
    val advertisedName = readableSpeakerName(txt["name"])
    val name = advertisedName ?: readableSpeakerName(friendlyName)
    // Older speakers can omit apps. A recognizable advertised speaker name is a usable
    // fallback for those models. An explicit list without app 5 identifies another service.
    if (apps?.contains(AUDIO_APP) != true && (apps != null || !advertisedSpeakerName(name))) return null

    val audioData = audioApplicationData(txt["appsdata"], apps.orEmpty())
    val stableId = audioData?.let(::parseMicoId)
        ?: decodeBase64(txt["idhash"])?.takeIf { it.isNotEmpty() }?.joinToString("") { "%02x".format(it) }
        ?: instanceName.lowercase(Locale.ROOT)
    val host = endpoint.hostAddress.orEmpty()
    val model = readableSpeakerName(txt["model"]).orEmpty()
    val controlPort = audioData?.let(::receiverControlPort).takeIf { advertisedName != null }
    // Software receivers advertise the authenticated control profile separately from
    // the older Mico speaker descriptor. A port alone does not identify that profile.
    val usesSecureMiPlay = controlPort != null && apps == listOf(AUDIO_APP) &&
        txt["version"] == "196608" && txt["flags"] == "CgE=" && txt["sec"] == "2"
    return DlnaRenderer(
        id = "miplay://$stableId",
        name = name.orEmpty(),
        descriptionUrl = "miplay://$host",
        host = host,
        avTransportControlUrl = "",
        renderingControlUrl = null,
        connectionManagerControlUrl = null,
        supportsMiPlay = true,
        miPlayIdentity = model.takeIf(String::isNotEmpty)?.let { SpeakerIdentity(model = it) },
        miPlayControlPort = controlPort,
        usesSecureMiPlay = usesSecureMiPlay,
    )
}

internal fun advertisementQuality(renderer: DlnaRenderer): Int =
    (if (renderer.usesSecureMiPlay) 16 else 0) +
        (if (renderer.miPlayControlPort == null) 0 else 8) +
        (if (renderer.id.endsWith("._lyra-mdns._udp.local") || renderer.id.endsWith("._mi-connect._udp.local")) 0 else 4) +
        (if (renderer.name.isBlank()) 0 else 2) + (if (renderer.miPlayIdentity == null) 0 else 1)

private fun decodeBase64(value: String?): ByteArray? = value?.let {
    runCatching { Base64.getDecoder().decode(it) }.getOrNull()
}

/** appsData stores length-prefixed app entries indexed by a 7-bit continuation bitmap. */
internal fun audioApplicationData(value: String?, apps: List<Int?>): ByteArray? {
    val bytes = decodeBase64(value) ?: return null
    var headers = 0
    do {
        if (headers == bytes.size) return null
    } while ((bytes[headers++].toInt() and 0x80) != 0)

    var offset = headers
    var audio: ByteArray? = null
    repeat(headers * 7) { slot ->
        if ((bytes[slot / 7].toInt() and (1 shl (slot % 7))) == 0) return@repeat
        if (offset >= bytes.size) return null
        val size = bytes[offset++].toInt() and 0xff
        if (size > bytes.size - offset) return null
        if (apps.getOrNull(slot) == AUDIO_APP) audio = bytes.copyOfRange(offset, offset + size)
        offset += size
    }
    return audio.takeIf { offset == bytes.size }
}

private fun parseMicoId(data: ByteArray): String? {
    val jsonStart = data.indexOf('{'.code.toByte())
    if (jsonStart < 0) return null
    return runCatching {
        JSONObject(data.decodeToString(jsonStart)).optJSONObject("mico")?.optString("device_id")?.trim()
            ?.takeIf { it.isNotEmpty() && it.length <= 128 && it.none(Char::isISOControl) }
    }.getOrNull()
}

// This app-5 descriptor carries the receiver's bound TCP control port. The DNS
// SRV port belongs to Mi Connect discovery and serves a different protocol.
private fun receiverControlPort(data: ByteArray): Int? {
    if (data.size != 4 || data[0] != 0x04.toByte() || data[1] != 0x83.toByte()) return null
    return ((data[2].toInt() and 0xff) shl 8 or (data[3].toInt() and 0xff)).takeIf { it != 0 }
}
