package com.vex.phonebackup.agent

import android.os.Process
import android.os.SystemClock
import com.topjohnwu.superuser.Shell
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.time.Instant
import java.util.UUID
import java.util.concurrent.TimeUnit

data class RootDiagnostics(
    val attemptId: String,
    val timestampUtc: String,
    val requestGrant: Boolean,
    val appUid: Int,
    val appGrantState: String,
    val cachedShellPresent: Boolean,
    val cachedShellAlive: Boolean,
    val cachedShellRoot: Boolean,
    val cachedShellClosed: Boolean,
    val shellExitCode: Int?,
    val shellSuccess: Boolean,
    val stdout: String,
    val stderr: String,
    val selinux: String,
    val exceptionType: String?,
    val exceptionMessage: String?,
    val durationMs: Long,
    val steps: List<String>
) {
    fun toJson(): JSONObject = JSONObject()
        .put("attemptId", attemptId)
        .put("timestampUtc", timestampUtc)
        .put("requestGrant", requestGrant)
        .put("appUid", appUid)
        .put("appGrantState", appGrantState)
        .put("cachedShellPresent", cachedShellPresent)
        .put("cachedShellAlive", cachedShellAlive)
        .put("cachedShellRoot", cachedShellRoot)
        .put("cachedShellClosed", cachedShellClosed)
        .put("shellExitCode", shellExitCode ?: JSONObject.NULL)
        .put("shellSuccess", shellSuccess)
        .put("stdout", stdout)
        .put("stderr", stderr)
        .put("selinux", selinux)
        .put("exceptionType", exceptionType ?: JSONObject.NULL)
        .put("exceptionMessage", exceptionMessage ?: JSONObject.NULL)
        .put("durationMs", durationMs)
        .put("steps", JSONArray(steps))
}

data class RootProbe(
    val available: Boolean,
    val granted: Boolean,
    val provider: String,
    val detail: String,
    val diagnostics: RootDiagnostics? = null
) {
    fun toJson(): JSONObject = JSONObject()
        .put("available", available)
        .put("granted", granted)
        .put("provider", provider)
        .put("detail", detail)
        .put("diagnostics", diagnostics?.toJson() ?: JSONObject.NULL)
}

internal data class RootProbeDecision(
    val available: Boolean,
    val granted: Boolean,
    val detail: String
)

internal fun classifyRootProbe(
    shellSuccess: Boolean,
    stdout: String,
    stderr: String,
    appGrantState: String,
    suAvailable: Boolean,
    failureType: String?,
    failureMessage: String?
): RootProbeDecision {
    val identityOutput = listOf(stdout, stderr)
        .filter { it.isNotBlank() }
        .joinToString("\n")
    val granted = shellSuccess && identityOutput.contains("uid=0")
    val available = granted ||
        appGrantState == "granted" ||
        appGrantState == "denied" ||
        suAvailable
    val detail = when {
        granted -> identityOutput.ifBlank { "Root shell returned uid=0" }
        failureType?.contains("timeout", ignoreCase = true) == true ||
            failureMessage?.contains("timeout", ignoreCase = true) == true ->
            "Root request timed out" +
                failureMessage?.takeIf { it.isNotBlank() }?.let { ": $it" }.orEmpty()
        !failureType.isNullOrBlank() ->
            "${failureType.substringAfterLast('.')}: ${failureMessage.orEmpty()}".trimEnd()
        identityOutput.isNotBlank() -> identityOutput
        appGrantState == "denied" -> "Root manager denied access for this application UID"
        else -> "su did not return a usable root shell"
    }
    return RootProbeDecision(available, granted, detail)
}

object RootCapabilities {
    @Volatile
    var lastProbe: RootProbe? = null
        private set

    fun hasCachedRootShell(): Boolean {
        val cached = Shell.getCachedShell() ?: return false
        return runCatching { cached.isAlive && cached.isRoot }.getOrDefault(false)
    }

    @Synchronized
    fun probe(requestGrant: Boolean = false): RootProbe {
        val attemptId = UUID.randomUUID().toString()
        val startedAt = SystemClock.elapsedRealtime()
        val steps = mutableListOf<String>()
        val cached = Shell.getCachedShell()
        val cachedPresent = cached != null
        val cachedAlive = runCatching { cached?.isAlive == true }.getOrDefault(false)
        val cachedRoot = runCatching { cached?.isRoot == true }.getOrDefault(false)
        val appGrantState = runCatching {
            when (Shell.isAppGrantedRoot()) {
                true -> "granted"
                false -> "denied"
                null -> "undetermined"
            }
        }.getOrElse { "error:${it.javaClass.simpleName}" }
        var cachedClosed = false

        steps += "cached_shell:present=$cachedPresent,alive=$cachedAlive,root=$cachedRoot"
        steps += "app_grant_state:$appGrantState"

        if (!requestGrant && cachedRoot) {
            val provider = detectProvider()
            return finish(
                RootProbe(
                    available = true,
                    granted = true,
                    provider = provider,
                    detail = "uid=0 (cached root shell)",
                    diagnostics = RootDiagnostics(
                        attemptId = attemptId,
                        timestampUtc = Instant.now().toString(),
                        requestGrant = false,
                        appUid = Process.myUid(),
                        appGrantState = appGrantState,
                        cachedShellPresent = cachedPresent,
                        cachedShellAlive = cachedAlive,
                        cachedShellRoot = cachedRoot,
                        cachedShellClosed = false,
                        shellExitCode = 0,
                        shellSuccess = true,
                        stdout = "uid=0 (cached root shell)",
                        stderr = "",
                        selinux = execute(listOf("getenforce"), 2).output.trim(),
                        exceptionType = null,
                        exceptionMessage = null,
                        durationMs = SystemClock.elapsedRealtime() - startedAt,
                        steps = steps + "result:cached_root"
                    )
                )
            )
        }

        if (!requestGrant) {
            val which = execute(listOf("sh", "-c", "command -v su"), 2)
            steps += "non_root_su_probe:exit=${which.exitCode}"
            val available = which.exitCode == 0 && which.output.isNotBlank()
            return finish(
                RootProbe(
                    available = available,
                    granted = false,
                    provider = if (available) "su" else "none",
                    detail = if (available) which.output.trim() else "Root grant was not requested",
                    diagnostics = RootDiagnostics(
                        attemptId = attemptId,
                        timestampUtc = Instant.now().toString(),
                        requestGrant = false,
                        appUid = Process.myUid(),
                        appGrantState = appGrantState,
                        cachedShellPresent = cachedPresent,
                        cachedShellAlive = cachedAlive,
                        cachedShellRoot = cachedRoot,
                        cachedShellClosed = false,
                        shellExitCode = which.exitCode,
                        shellSuccess = available,
                        stdout = which.output.trim(),
                        stderr = "",
                        selinux = execute(listOf("getenforce"), 2).output.trim(),
                        exceptionType = null,
                        exceptionMessage = null,
                        durationMs = SystemClock.elapsedRealtime() - startedAt,
                        steps = steps + "result:no_grant_request"
                    )
                )
            )
        }

        if (cached != null && !cachedRoot) {
            val closed = runCatching {
                cached.close()
                true
            }
            cachedClosed = closed.getOrDefault(false)
            steps += if (cachedClosed) {
                "cached_non_root_shell:closed"
            } else {
                "cached_non_root_shell:close_failed:${closed.exceptionOrNull()?.javaClass?.simpleName}"
            }
        }

        var shellExitCode: Int? = null
        var shellSuccess = false
        var stdout = ""
        var stderr = ""
        var failure: Throwable? = null
        try {
            steps += "root_shell:request"
            val shell = Shell.getShell()
            steps += "root_shell:created,status=${shell.status},root=${shell.isRoot}"
            val outputLines = mutableListOf<String>()
            val errorLines = mutableListOf<String>()
            val result = shell.newJob()
                .add("id")
                .to(outputLines, errorLines)
                .exec()
            shellExitCode = result.code
            shellSuccess = result.isSuccess
            stdout = outputLines.joinToString("\n").trim()
            stderr = errorLines.joinToString("\n").trim()
            steps += "id:exit=$shellExitCode,success=$shellSuccess"
        } catch (error: Throwable) {
            failure = error
            steps += "root_shell:exception=${error.javaClass.name}"
        }

        val which = execute(listOf("sh", "-c", "command -v su"), 2)
        val decision = classifyRootProbe(
            shellSuccess = shellSuccess,
            stdout = stdout,
            stderr = stderr,
            appGrantState = appGrantState,
            suAvailable = which.exitCode == 0 && which.output.isNotBlank(),
            failureType = failure?.javaClass?.name,
            failureMessage = failure?.message
        )
        val provider = if (decision.granted) detectProvider() else {
            when {
                appGrantState == "denied" -> "KernelSU/Magisk/APatch"
                decision.available -> "su"
                else -> "none"
            }
        }
        steps += "result:${if (decision.granted) "granted" else "denied"}"

        return finish(
            RootProbe(
                available = decision.available,
                granted = decision.granted,
                provider = provider,
                detail = decision.detail,
                diagnostics = RootDiagnostics(
                    attemptId = attemptId,
                    timestampUtc = Instant.now().toString(),
                    requestGrant = true,
                    appUid = Process.myUid(),
                    appGrantState = appGrantState,
                    cachedShellPresent = cachedPresent,
                    cachedShellAlive = cachedAlive,
                    cachedShellRoot = cachedRoot,
                    cachedShellClosed = cachedClosed,
                    shellExitCode = shellExitCode,
                    shellSuccess = shellSuccess,
                    stdout = stdout,
                    stderr = stderr,
                    selinux = execute(listOf("getenforce"), 2).output.trim(),
                    exceptionType = failure?.javaClass?.name,
                    exceptionMessage = failure?.message,
                    durationMs = SystemClock.elapsedRealtime() - startedAt,
                    steps = steps
                )
            )
        )
    }

    private fun finish(probe: RootProbe): RootProbe {
        lastProbe = probe
        val diagnostics = probe.diagnostics
        AgentDiagnostics.log(
            level = if (probe.granted) "info" else if (diagnostics?.requestGrant == true) "warn" else "info",
            component = "root",
            eventCode = "probe",
            message = probe.detail,
            operationId = diagnostics?.attemptId,
            durationMs = diagnostics?.durationMs,
            result = if (probe.granted) "granted" else if (probe.available) "denied" else "unavailable",
            fields = mapOf(
                "available" to probe.available,
                "granted" to probe.granted,
                "provider" to probe.provider,
                "appUid" to diagnostics?.appUid,
                "appGrantState" to diagnostics?.appGrantState,
                "cachedShellPresent" to diagnostics?.cachedShellPresent,
                "cachedShellRoot" to diagnostics?.cachedShellRoot,
                "cachedShellClosed" to diagnostics?.cachedShellClosed,
                "shellExitCode" to diagnostics?.shellExitCode,
                "shellSuccess" to diagnostics?.shellSuccess,
                "stdout" to diagnostics?.stdout,
                "stderr" to diagnostics?.stderr,
                "selinux" to diagnostics?.selinux,
                "exceptionType" to diagnostics?.exceptionType,
                "exceptionMessage" to diagnostics?.exceptionMessage
            )
        )
        return probe
    }

    private fun detectProvider(): String {
        val probes = listOf(
            "KernelSU" to "/data/adb/ksu",
            "Magisk" to "/data/adb/magisk",
            "APatch" to "/data/adb/ap"
        )
        return probes.firstOrNull { (_, path) ->
            runCatching { Shell.cmd("test -e $path").exec().isSuccess }.getOrDefault(false)
        }?.first ?: "su"
    }

    private fun execute(command: List<String>, timeoutSeconds: Long): CommandResult = runCatching {
        val process = ProcessBuilder(command).redirectErrorStream(true).start()
        val output = BufferedReader(InputStreamReader(process.inputStream)).use { it.readText() }
        val finished = process.waitFor(timeoutSeconds, TimeUnit.SECONDS)
        if (!finished) process.destroyForcibly()
        CommandResult(if (finished) process.exitValue() else -1, output)
    }.getOrElse { CommandResult(-1, it.message.orEmpty()) }

    private data class CommandResult(val exitCode: Int, val output: String)
}
