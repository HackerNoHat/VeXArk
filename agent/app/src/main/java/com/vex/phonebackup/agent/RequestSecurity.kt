package com.vex.phonebackup.agent

import android.util.Base64
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import org.json.JSONObject
import org.json.JSONArray
import java.security.MessageDigest
import java.util.LinkedHashMap
import kotlin.math.abs

object RequestSecurity {
    private const val MAX_CLOCK_SKEW_SECONDS = 120L
    private const val MAX_NONCES = 2048
    private val seenNonces = object : LinkedHashMap<String, Long>(MAX_NONCES, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, Long>?): Boolean =
            size > MAX_NONCES
    }

    fun verify(request: JSONObject): String? = runCatching {
        val version = request.optInt("protocolVersion", -1)
        val requestId = request.optString("requestId")
        val command = request.optString("command")
        val desktopKey = request.optString("desktopKey")
        val timestamp = request.optLong("timestamp", 0)
        val nonce = request.optString("nonce")
        val signatureText = request.optString("signature")
        val payloadHash = request.optString("payloadHash")
        if (version != AgentForegroundService.PROTOCOL_VERSION) return "protocol_version_mismatch"
        if (requestId.isBlank() || command.isBlank() || nonce.isBlank()) return "unsigned_request"
        val now = System.currentTimeMillis() / 1000
        if (abs(now - timestamp) > MAX_CLOCK_SKEW_SECONDS) return "request_expired"

        val actualPayloadHash = MessageDigest.getInstance("SHA-256")
            .digest(canonicalJson(request.opt("payload") ?: JSONObject.NULL).toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it) }
        if (!actualPayloadHash.equals(payloadHash, ignoreCase = true)) return "payload_tampered"

        val replayKey = "$desktopKey:$nonce"
        synchronized(seenNonces) {
            if (seenNonces.containsKey(replayKey)) return "replayed_request"
        }
        val publicKey = Base64.decode(desktopKey, Base64.NO_WRAP)
        val signature = Base64.decode(signatureText, Base64.NO_WRAP)
        if (publicKey.size != Ed25519PublicKeyParameters.KEY_SIZE) return "invalid_desktop_key"
        val message = canonical(
            version, requestId, command, timestamp, nonce, payloadHash
        ).toByteArray(Charsets.UTF_8)
        val verifier = Ed25519Signer()
        verifier.init(false, Ed25519PublicKeyParameters(publicKey, 0))
        verifier.update(message, 0, message.size)
        if (!verifier.verifySignature(signature)) return "invalid_signature"
        synchronized(seenNonces) { seenNonces[replayKey] = timestamp }
        null
    }.getOrElse { "invalid_signature" }

    private fun canonical(
        version: Int,
        requestId: String,
        command: String,
        timestamp: Long,
        nonce: String,
        payloadHash: String
    ): String = "$version\n$requestId\n$command\n$timestamp\n$nonce\n$payloadHash"

    private fun canonicalJson(value: Any?): String = when (value) {
        null, JSONObject.NULL -> "null"
        is JSONObject -> value.keys().asSequence().toList().sorted().joinToString(
            prefix = "{",
            postfix = "}",
            separator = ","
        ) { key -> "${JSONObject.quote(key)}:${canonicalJson(value.get(key))}" }
        is JSONArray -> (0 until value.length()).joinToString(
            prefix = "[",
            postfix = "]",
            separator = ","
        ) { index -> canonicalJson(value.get(index)) }
        is String -> JSONObject.quote(value)
        is Boolean, is Number -> value.toString()
        else -> JSONObject.quote(value.toString())
    }
}
