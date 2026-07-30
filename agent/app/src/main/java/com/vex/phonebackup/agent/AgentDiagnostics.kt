package com.vex.phonebackup.agent

import android.content.Context
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.time.Instant
import java.util.ArrayDeque
import java.util.Locale

data class AgentDiagnosticEvent(
    val timestampUtc: String,
    val level: String,
    val component: String,
    val eventCode: String,
    val operationId: String?,
    val requestId: String?,
    val durationMs: Long?,
    val result: String?,
    val message: String,
    val errorType: String?,
    val stackTrace: String?,
    val fields: Map<String, String>
) {
    fun toJson(): JSONObject = JSONObject()
        .put("timestampUtc", timestampUtc)
        .put("level", level)
        .put("component", component)
        .put("eventCode", eventCode)
        .put("operationId", operationId ?: JSONObject.NULL)
        .put("requestId", requestId ?: JSONObject.NULL)
        .put("durationMs", durationMs ?: JSONObject.NULL)
        .put("result", result ?: JSONObject.NULL)
        .put("message", message)
        .put("errorType", errorType ?: JSONObject.NULL)
        .put("stackTrace", stackTrace ?: JSONObject.NULL)
        .put("fields", JSONObject(fields))

    fun displayLine(): String {
        val time = timestampUtc.substringAfter('T').substringBeforeLast('.')
        val suffix = result?.let { " [$it]" }.orEmpty()
        return "$time ${level.uppercase(Locale.ROOT)} $component/$eventCode$suffix — $message"
    }
}

object AgentDiagnostics {
    private const val LOG_TAG = "VeXArkAgentDev"
    private const val MAX_MEMORY_EVENTS = 500
    private const val MAX_FILE_BYTES = 1024L * 1024
    private const val MAX_FILES = 4
    private val gate = Any()
    private val memory = ArrayDeque<AgentDiagnosticEvent>()
    private var directory: File? = null
    private val secretAssignmentRegex = Regex(
        """(?i)(["']?(?:password|recovery(?:code)?|privatekey|publickey|desktopkey|""" +
            """signature|nonce|pairingcode|sessionkey|approvaltoken)["']?\s*(?:=|:)\s*["']?)""" +
            """([^"'\s,}\]]+)"""
    )
    private val pairingCodeRegex = Regex(
        """(?i)(\bpairing\s*code\b\s*(?:=|:)?\s*)\d{6,12}\b"""
    )

    fun initialize(context: Context) {
        synchronized(gate) {
            if (directory != null) return
            directory = File(context.filesDir, "diagnostics").also { it.mkdirs() }
        }
        log(
            level = "info",
            component = "agent",
            eventCode = "startup",
            message = "Agent diagnostics initialized",
            fields = mapOf(
                "channel" to BuildConfig.BUILD_CHANNEL,
                "buildId" to BuildConfig.BUILD_ID,
                "packageName" to BuildConfig.APPLICATION_ID,
                "versionName" to BuildConfig.VERSION_NAME,
                "versionCode" to BuildConfig.VERSION_CODE.toString(),
                "port" to BuildConfig.AGENT_PORT.toString()
            )
        )
    }

    fun log(
        level: String,
        component: String,
        eventCode: String,
        message: String,
        operationId: String? = null,
        requestId: String? = null,
        durationMs: Long? = null,
        result: String? = null,
        error: Throwable? = null,
        fields: Map<String, Any?> = emptyMap()
    ) {
        val safeFields = fields.entries.associate { (key, value) ->
            key to redact(key, value?.toString().orEmpty())
        }
        val event = AgentDiagnosticEvent(
            timestampUtc = Instant.now().toString(),
            level = level.lowercase(Locale.ROOT),
            component = component,
            eventCode = eventCode,
            operationId = operationId,
            requestId = requestId,
            durationMs = durationMs,
            result = result,
            message = redactText(message),
            errorType = error?.javaClass?.name,
            stackTrace = error?.stackTraceToString()?.let(::redactText),
            fields = safeFields
        )
        synchronized(gate) {
            memory.addLast(event)
            while (memory.size > MAX_MEMORY_EVENTS) memory.removeFirst()
            appendLocked(event)
        }
        AgentState.diagnosticRevision++
        when (event.level) {
            "error" -> Log.e(LOG_TAG, event.toJson().toString(), error)
            "warn" -> Log.w(LOG_TAG, event.toJson().toString(), error)
            else -> Log.i(LOG_TAG, event.toJson().toString())
        }
    }

    fun snapshot(): List<AgentDiagnosticEvent> = synchronized(gate) { memory.toList() }

    fun snapshotJson(): JSONObject {
        val events = JSONArray()
        snapshot().forEach { events.put(it.toJson()) }
        return JSONObject()
            .put("channel", BuildConfig.BUILD_CHANNEL)
            .put("buildId", BuildConfig.BUILD_ID)
            .put("packageName", BuildConfig.APPLICATION_ID)
            .put("versionName", BuildConfig.VERSION_NAME)
            .put("versionCode", BuildConfig.VERSION_CODE)
            .put("agentPort", BuildConfig.AGENT_PORT)
            .put("diagnosticsEnabled", BuildConfig.DIAGNOSTICS_ENABLED)
            .put("events", events)
            .put(
                "lastRootProbe",
                RootCapabilities.lastProbe?.toJson() ?: JSONObject.NULL
            )
    }

    fun reportText(): String {
        val root = RootCapabilities.lastProbe
        return buildString {
            appendLine("VeXArk Agent diagnostics")
            appendLine("channel=${BuildConfig.BUILD_CHANNEL}")
            appendLine("package=${BuildConfig.APPLICATION_ID}")
            appendLine("version=${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})")
            appendLine("buildId=${BuildConfig.BUILD_ID}")
            appendLine("uid=${android.os.Process.myUid()}")
            appendLine("port=${BuildConfig.AGENT_PORT}")
            if (root != null) {
                appendLine(
                    "root=${root.granted} available=${root.available} " +
                        "provider=${root.provider}"
                )
                appendLine("rootDetail=${root.detail}")
                root.diagnostics?.let {
                    appendLine("rootAttempt=${it.attemptId}")
                    appendLine("rootExitCode=${it.shellExitCode}")
                    appendLine("rootStdout=${it.stdout}")
                    appendLine("rootStderr=${it.stderr}")
                    appendLine("rootException=${it.exceptionType}: ${it.exceptionMessage}")
                }
            }
            appendLine()
            snapshot().forEach { appendLine(it.displayLine()) }
        }
    }

    fun clear() {
        synchronized(gate) {
            memory.clear()
            directory?.listFiles()
                ?.filter { it.name.startsWith("agent.jsonl") }
                ?.forEach { runCatching { it.delete() } }
        }
        AgentState.diagnosticRevision++
        log(
            level = "info",
            component = "diagnostics",
            eventCode = "cleared",
            message = "Agent diagnostic log cleared"
        )
    }

    private fun appendLocked(event: AgentDiagnosticEvent) {
        val folder = directory ?: return
        val current = File(folder, "agent.jsonl")
        val line = event.toJson().toString() + "\n"
        if (current.exists() && current.length() + line.toByteArray().size > MAX_FILE_BYTES)
            rotateLocked(folder)
        runCatching { current.appendText(line, Charsets.UTF_8) }
            .onFailure { Log.e(LOG_TAG, "Failed to persist diagnostic event", it) }
    }

    private fun rotateLocked(folder: File) {
        rotateDiagnosticFiles(folder, MAX_FILES)
    }

    private fun redact(key: String, value: String): String {
        val normalized = key.lowercase(Locale.ROOT)
        return if (listOf(
                "password", "recovery", "privatekey", "publickey", "desktopkey",
                "signature", "nonce", "pairingcode", "sessionkey", "approvaltoken"
            ).any(normalized::contains)
        ) {
            "[redacted]"
        } else {
            redactText(value.take(16 * 1024))
        }
    }

    private fun redactText(value: String): String {
        val secretsRedacted = secretAssignmentRegex.replace(value) {
            it.groupValues[1] + "[redacted]"
        }
        return pairingCodeRegex.replace(secretsRedacted) {
            it.groupValues[1] + "[redacted]"
        }
    }
}

internal fun rotateDiagnosticFiles(folder: File, maxFiles: Int) {
    require(maxFiles >= 2)
    for (index in maxFiles - 1 downTo 1) {
        val destination = File(folder, "agent.jsonl.$index")
        val source = if (index == 1) {
            File(folder, "agent.jsonl")
        } else {
            File(folder, "agent.jsonl.${index - 1}")
        }
        if (!source.exists()) continue
        if (destination.exists()) destination.delete()
        check(source.renameTo(destination)) {
            "Cannot rotate ${source.name} to ${destination.name}"
        }
    }
}
