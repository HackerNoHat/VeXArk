package com.vex.phonebackup.agent

import com.topjohnwu.superuser.Shell
import java.io.BufferedReader
import java.io.InputStreamReader
import java.util.concurrent.TimeUnit

data class RootProbe(
    val available: Boolean,
    val granted: Boolean,
    val provider: String,
    val detail: String
)

object RootCapabilities {
    fun probe(requestGrant: Boolean = false): RootProbe {
        val which = execute(listOf("sh", "-c", "command -v su"), 2)
        if (which.exitCode != 0 || which.output.isBlank()) {
            return RootProbe(false, false, "none", "su binary not found")
        }
        if (!requestGrant) {
            return RootProbe(true, false, "su", which.output.trim())
        }
        val id = runCatching { Shell.cmd("id").exec() }.getOrNull()
        val output = id?.out?.joinToString("\n").orEmpty() +
            id?.err?.joinToString("\n").orEmpty()
        val granted = id?.isSuccess == true && output.contains("uid=0")
        return RootProbe(true, granted, if (granted) detectProvider() else "su", output.trim())
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
